using Discovery.Api.Services.Quartz;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Quartz;

namespace Discovery.Api.Services.BackgroundProcessing;

/// <summary>
/// Aplica no scheduler o tick definido na configuração global (banco) sem restart.
///
/// Decisões:
/// - o tick é global (um trigger é global); o INTERVALO por cliente continua
///   sendo verificado dentro do ciclo;
/// - o appsettings permanece como kill switch de operação e fallback do delay
///   de startup;
/// - só reprograma quando o tick muda (evita churn de trigger) e faz isso uma
///   vez por instância (o store do Quartz é em memória).
/// </summary>
public sealed class BackgroundProcessingScheduleService(
    IBackgroundProcessingScheduler scheduler,
    IConfigurationResolver configurationResolver,
    IConfiguration configuration,
    ILogger<BackgroundProcessingScheduleService> logger) : IBackgroundProcessingScheduleService
{
    public const string MetricsProcess = "technician_metrics";
    public const string TriageProcess = "ticket_triage";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private BackgroundScheduleSnapshot _current = new(DateTimeOffset.MinValue, []);

    public async Task<BackgroundScheduleSnapshot> ApplyAsync(bool force = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = await configurationResolver.ResolveBackgroundProcessingAsync(null, ct);
            var plans = BuildPlans(settings);
            var states = new List<BackgroundScheduleProcessState>(plans.Count);

            foreach (var plan in plans)
            {
                if (!plan.Enabled)
                {
                    // Kill switch: o job nem foi agendado no startup.
                    states.Add(plan with { NextFireTimeUtc = await NextFireAsync(plan, ct) });
                    continue;
                }

                var changed = plan.AppliedTickSeconds is null || plan.AppliedTickSeconds != plan.TickSeconds;
                if (!changed && !force)
                {
                    states.Add(plan with { NextFireTimeUtc = await NextFireAsync(plan, ct) });
                    continue;
                }

                var nextFire = await scheduler.RescheduleAsync(
                    plan.JobName, plan.JobGroup, plan.TickSeconds, StartupDelaySeconds(plan.JobName), ct);

                if (nextFire is null)
                {
                    logger.LogWarning(
                        "Processamento {Process}: job {Job} não está agendado (kill switch no startup?); tick {Tick}s valerá no próximo restart.",
                        plan.Process, plan.JobName, plan.TickSeconds);

                    states.Add(plan with { Enabled = false, NextFireTimeUtc = null });
                    continue;
                }

                logger.LogInformation(
                    "Processamento {Process}: tick reprogramado para {Tick}s (antes {Previous}).",
                    plan.Process, plan.TickSeconds, plan.AppliedTickSeconds);

                states.Add(plan with { NextFireTimeUtc = nextFire, AppliedTickSeconds = plan.TickSeconds });
            }

            _current = new BackgroundScheduleSnapshot(DateTimeOffset.UtcNow, states);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BackgroundScheduleSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        // Somente leitura: reporta o desejado (banco), o aplicado e o próximo disparo.
        var settings = await configurationResolver.ResolveBackgroundProcessingAsync(null, ct);
        var plans = BuildPlans(settings);

        var states = new List<BackgroundScheduleProcessState>(plans.Count);
        foreach (var plan in plans)
            states.Add(plan with { NextFireTimeUtc = await NextFireAsync(plan, ct) });

        return new BackgroundScheduleSnapshot(_current.AppliedAt, states);
    }

    private IReadOnlyList<BackgroundScheduleProcessState> BuildPlans(BackgroundProcessingSettings settings)
    {
        var metricsEnabled = configuration.GetValue<bool?>("BackgroundJobs:TechnicianMetrics:Enabled") ?? true;
        var triageEnabled = configuration.GetValue<bool?>("BackgroundJobs:AiTicketAssignment:Enabled") ?? true;

        return
        [
            Plan(MetricsProcess, TechnicianMetricsRefreshJob.Key, settings.Metrics.TickSeconds, metricsEnabled),
            Plan(TriageProcess, AiTicketAssignmentJob.Key, settings.Triage.TickSeconds, triageEnabled)
        ];
    }

    private BackgroundScheduleProcessState Plan(
        string process, JobKey jobKey, int tickSeconds, bool enabled)
        => new(
            process,
            jobKey.Name,
            jobKey.Group,
            tickSeconds,
            _current.Processes.FirstOrDefault(state => state.JobName == jobKey.Name)?.TickSeconds,
            enabled,
            null);

    private Task<DateTimeOffset?> NextFireAsync(BackgroundScheduleProcessState plan, CancellationToken ct)
        => scheduler.GetNextFireTimeAsync(plan.JobName, plan.JobGroup, ct);

    private int StartupDelaySeconds(string jobName)
        => jobName == TechnicianMetricsRefreshJob.Key.Name
            ? Math.Max(0, configuration.GetValue<int?>("BackgroundJobs:TechnicianMetrics:StartupDelaySeconds") ?? 60)
            : Math.Max(0, configuration.GetValue<int?>("BackgroundJobs:AiTicketAssignment:StartupDelaySeconds") ?? 20);
}

using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Sincroniza o tick dos processamentos em segundo plano com a configuração
/// global (banco). Redundante por desenho: além do startup e do salvamento da
/// configuração, cobre edição direta no banco e instâncias que acabaram de subir.
///
/// Configuração (appsettings):
/// - BackgroundJobs:BackgroundProcessingScheduleSync:Enabled (default: true)
/// - BackgroundJobs:BackgroundProcessingScheduleSync:IntervalSeconds (default: 120, piso 30)
/// </summary>
[DisallowConcurrentExecution]
public sealed class BackgroundProcessingScheduleSyncJob : IJob
{
    public static readonly JobKey Key = new("background-processing-schedule-sync", "tickets");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var config = context.GetScopedService<IConfiguration>();
        var logger = context.GetLogger<BackgroundProcessingScheduleSyncJob>();
        var ct = context.CancellationToken;

        if (!(config.GetValue<bool?>("BackgroundJobs:BackgroundProcessingScheduleSync:Enabled") ?? true))
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var scheduleService = scope.ServiceProvider.GetRequiredService<IBackgroundProcessingScheduleService>();

        try
        {
            var snapshot = await scheduleService.ApplyAsync(force: false, ct);
            context.Result = snapshot;

            foreach (var process in snapshot.Processes.Where(p => p.Enabled && p.AppliedTickSeconds != p.TickSeconds))
            {
                logger.LogInformation(
                    "Agendamento de {Process}: tick aplicado {Applied}s (desejado {Tick}s).",
                    process.Process, process.AppliedTickSeconds, process.TickSeconds);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao sincronizar o agendamento dos processamentos em segundo plano.");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}

using Discovery.Core.Interfaces;
using Quartz;

namespace Discovery.Api.Services.BackgroundProcessing;

/// <summary>Implementação Quartz da abstração de reprogramação de triggers.</summary>
public sealed class QuartzBackgroundProcessingScheduler(ISchedulerFactory schedulerFactory)
    : IBackgroundProcessingScheduler
{
    public async Task<DateTimeOffset?> RescheduleAsync(
        string jobName, string jobGroup, int intervalSeconds, int startupDelaySeconds,
        CancellationToken ct = default)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        var triggerKey = new TriggerKey($"{jobName}-trigger", jobGroup);

        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .StartAt(DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, startupDelaySeconds)))
            .WithSimpleSchedule(schedule => schedule
                .WithIntervalInSeconds(Math.Max(1, intervalSeconds))
                .RepeatForever())
            .WithDescription("Reprogramado a partir da configuração de processamento em segundo plano")
            .Build();

        // RescheduleJob devolve o next fire ANTERIOR (null quando o trigger não existe).
        await scheduler.RescheduleJob(triggerKey, trigger, ct);
        return await GetNextFireTimeAsync(jobName, jobGroup, ct);
    }

    public async Task<DateTimeOffset?> GetNextFireTimeAsync(
        string jobName, string jobGroup, CancellationToken ct = default)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        var trigger = await scheduler.GetTrigger(new TriggerKey($"{jobName}-trigger", jobGroup), ct);
        return trigger?.GetNextFireTimeUtc();
    }
}

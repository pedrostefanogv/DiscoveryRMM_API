using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job listener that records execution history and logs lifecycle events.
/// Registered globally in QuartzServiceCollectionExtensions.
/// </summary>
public sealed class JobExecutionHistoryListener : IJobListener
{
    public string Name => "Discovery-JobHistoryListener";

    public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken ct)
    {
        context.Put("startedAt", DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken ct)
    {
        var startedAt = context.Get("startedAt") as DateTime? ?? context.FireTimeUtc.UtcDateTime;
        var completedAt = DateTime.UtcNow;
        var duration = completedAt - startedAt;

        var record = new JobExecutionRecord(
            StartedAtUtc: startedAt,
            CompletedAtUtc: completedAt,
            Duration: duration,
            Succeeded: jobException == null,
            ErrorMessage: jobException?.Message,
            Result: context.Result);

        JobExecutionHistoryStore.Record(context.JobDetail.Key, record);

        return Task.CompletedTask;
    }
}

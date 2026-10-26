using System.Collections.Concurrent;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job listener that records execution history and logs lifecycle events.
/// Registered globally in QuartzServiceCollectionExtensions.
/// </summary>
public sealed class JobExecutionHistoryListener : IJobListener
{
    public string Name => "Discovery-JobHistoryListener";

    // Quartz 4 removeu context.Put/Get: armazenamos o início por FireInstanceId.
    private static readonly ConcurrentDictionary<string, DateTime> StartedAtByFireInstance = new();

    public ValueTask JobToBeExecuted(IJobExecutionContext context, CancellationToken ct)
    {
        StartedAtByFireInstance[context.FireInstanceId] = DateTime.UtcNow;
        return ValueTask.CompletedTask;
    }

    public ValueTask JobExecutionVetoed(IJobExecutionContext context, CancellationToken ct)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken ct)
    {
        var startedAt = StartedAtByFireInstance.TryRemove(context.FireInstanceId, out var s)
            ? s
            : context.FireTimeUtc.UtcDateTime;
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

        return ValueTask.CompletedTask;
    }
}

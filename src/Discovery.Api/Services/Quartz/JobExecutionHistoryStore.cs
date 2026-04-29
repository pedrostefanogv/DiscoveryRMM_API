using System.Collections.Concurrent;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// In-memory store for job execution history.
/// Populated by JobExecutionHistoryListener.
/// Exposed via GET /api/v1/admin/jobs/{group}/{name} for dashboard visibility.
/// </summary>
public static class JobExecutionHistoryStore
{
    private static readonly ConcurrentDictionary<JobKey, List<JobExecutionRecord>> _store = new();

    public static void Record(JobKey key, JobExecutionRecord record)
    {
        var list = _store.GetOrAdd(key, _ => []);
        lock (list)
        {
            list.Add(record);
            // Keep last 50 entries per job
            if (list.Count > 50)
                list.RemoveRange(0, list.Count - 50);
        }
    }

    public static List<JobExecutionRecord> GetHistory(JobKey key)
    {
        return _store.TryGetValue(key, out var list) ? [.. list] : [];
    }
}

public sealed record JobExecutionRecord(
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    TimeSpan? Duration,
    bool Succeeded,
    string? ErrorMessage,
    object? Result);

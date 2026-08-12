namespace Discovery.Core.Interfaces;

/// <summary>
/// Queue for triggering asynchronous label reprocessing across all agents.
/// Enqueuing returns immediately so the HTTP request does not block on a
/// potentially long-running batch operation.
/// </summary>
public interface ILabelReprocessQueue
{
    ValueTask EnqueueAsync(CancellationToken cancellationToken = default);
}

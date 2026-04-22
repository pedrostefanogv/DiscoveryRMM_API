namespace Discovery.Core.Interfaces;

public interface IAiChatJobQueue
{
    ValueTask EnqueueAsync(Guid jobId, Guid agentId, CancellationToken cancellationToken = default);
}
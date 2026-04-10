using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

public interface ISyncPingDispatchQueue
{
    ValueTask EnqueueAsync(SyncInvalidationPingDto ping, CancellationToken cancellationToken = default);
}

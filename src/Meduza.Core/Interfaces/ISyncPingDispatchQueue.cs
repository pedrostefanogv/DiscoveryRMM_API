using Meduza.Core.DTOs;

namespace Meduza.Core.Interfaces;

public interface ISyncPingDispatchQueue
{
    ValueTask EnqueueAsync(SyncInvalidationPingDto ping, CancellationToken cancellationToken = default);
}

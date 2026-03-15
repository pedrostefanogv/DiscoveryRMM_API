using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface ISyncPingDeliveryRepository
{
    Task<SyncPingDelivery> CreateSentAsync(Guid eventId, Guid agentId, SyncResourceType resource, string revision);
    Task<SyncPingDelivery> UpsertAckAsync(Guid eventId, Guid agentId, SyncPingAckRequest request, DateTime acknowledgedAt);
    Task<bool> IsAcknowledgedAsync(Guid eventId, Guid agentId, string revision);
}

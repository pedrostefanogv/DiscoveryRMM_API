using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class SyncPingDeliveryRepository : ISyncPingDeliveryRepository
{
    private readonly MeduzaDbContext _db;

    public SyncPingDeliveryRepository(MeduzaDbContext db) => _db = db;

    public async Task<SyncPingDelivery> CreateSentAsync(Guid eventId, Guid agentId, SyncResourceType resource, string revision)
    {
        var existing = await _db.SyncPingDeliveries
            .SingleOrDefaultAsync(x => x.EventId == eventId && x.AgentId == agentId && x.Revision == revision);

        if (existing is not null)
            return existing;

        var delivery = new SyncPingDelivery
        {
            Id = IdGenerator.NewId(),
            EventId = eventId,
            AgentId = agentId,
            Resource = resource,
            Revision = revision,
            Status = SyncPingDeliveryStatus.Sent,
            SentAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.SyncPingDeliveries.Add(delivery);
        await _db.SaveChangesAsync();
        return delivery;
    }

    public async Task<SyncPingDelivery> UpsertAckAsync(Guid eventId, Guid agentId, SyncPingAckRequest request, DateTime acknowledgedAt)
    {
        var status = request.Status.Equals("success", StringComparison.OrdinalIgnoreCase)
            ? SyncPingDeliveryStatus.Acknowledged
            : SyncPingDeliveryStatus.Failed;

        var delivery = await _db.SyncPingDeliveries
            .SingleOrDefaultAsync(x => x.EventId == eventId && x.AgentId == agentId && x.Revision == request.Revision);

        if (delivery is null)
        {
            delivery = new SyncPingDelivery
            {
                Id = IdGenerator.NewId(),
                EventId = eventId,
                AgentId = agentId,
                Resource = request.Resource,
                Revision = request.Revision,
                Status = status,
                SentAt = request.ReceivedAt ?? acknowledgedAt,
                AcknowledgedAt = acknowledgedAt,
                AckMetadataJson = request.AckMetadataJson,
                ErrorCode = request.ErrorCode,
                ErrorMessage = request.ErrorMessage,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _db.SyncPingDeliveries.Add(delivery);
            await _db.SaveChangesAsync();
            return delivery;
        }

        delivery.Status = status;
        delivery.AcknowledgedAt = acknowledgedAt;
        delivery.AckMetadataJson = request.AckMetadataJson;
        delivery.ErrorCode = request.ErrorCode;
        delivery.ErrorMessage = request.ErrorMessage;
        delivery.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return delivery;
    }

    public async Task<bool> IsAcknowledgedAsync(Guid eventId, Guid agentId, string revision)
    {
        return await _db.SyncPingDeliveries
            .AnyAsync(x => x.EventId == eventId
                && x.AgentId == agentId
                && x.Revision == revision
                && x.Status == SyncPingDeliveryStatus.Acknowledged);
    }
}

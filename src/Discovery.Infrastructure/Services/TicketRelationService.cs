using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class TicketRelationService : ITicketRelationService
{
    private readonly DiscoveryDbContext _db;
    private readonly ITicketRepository _ticketRepo;
    private readonly IActivityLogService _activityLogService;
    private readonly ILogger<TicketRelationService> _logger;

    public TicketRelationService(
        DiscoveryDbContext db,
        ITicketRepository ticketRepo,
        IActivityLogService activityLogService,
        ILogger<TicketRelationService> logger)
    {
        _db = db;
        _ticketRepo = ticketRepo;
        _activityLogService = activityLogService;
        _logger = logger;
    }

    public async Task<TicketRelation> CreateRelationAsync(
        Guid sourceTicketId,
        Guid targetTicketId,
        TicketRelationType relationType,
        string? createdBy,
        CancellationToken ct = default)
    {
        if (sourceTicketId == targetTicketId)
            throw new InvalidOperationException("Cannot relate a ticket to itself.");

        var source = await _ticketRepo.GetByIdAsync(sourceTicketId);
        var target = await _ticketRepo.GetByIdAsync(targetTicketId);
        if (source is null) throw new InvalidOperationException($"Source ticket {sourceTicketId} not found.");
        if (target is null) throw new InvalidOperationException($"Target ticket {targetTicketId} not found.");

        // Verificar se relação já existe
        var existing = await _db.TicketRelations
            .FirstOrDefaultAsync(r =>
                r.SourceTicketId == sourceTicketId
                && r.TargetTicketId == targetTicketId
                && r.RelationTypeValue == (int)relationType, ct);

        if (existing is not null)
            return existing;

        // Validar bloqueio de ciclos para Blocks
        if (relationType == TicketRelationType.Blocks)
        {
            var isBlockedInReverse = await _db.TicketRelations
                .AnyAsync(r =>
                    r.SourceTicketId == targetTicketId
                    && r.TargetTicketId == sourceTicketId
                    && r.RelationTypeValue == (int)TicketRelationType.Blocks, ct);

            if (isBlockedInReverse)
                throw new InvalidOperationException($"Circular block detected: ticket #{sourceTicketId} is already blocked by ticket #{targetTicketId}.");
        }

        var relation = new TicketRelation
        {
            Id = IdGenerator.NewId(),
            SourceTicketId = sourceTicketId,
            TargetTicketId = targetTicketId,
            RelationTypeValue = (int)relationType,
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow
        };

        _db.TicketRelations.Add(relation);
        await _db.SaveChangesAsync(ct);

        await _activityLogService.LogActivityAsync(
            sourceTicketId,
            TicketActivityType.TicketRelationAdded,
            null, null,
            $"relation:{relationType}:{targetTicketId}",
            $"Relação '{relationType}' adicionada com ticket #{targetTicketId}");

        _logger.LogInformation("Relation {Type} created: {SourceId} -> {TargetId}",
            relationType, sourceTicketId, targetTicketId);

        return relation;
    }

    public async Task<List<TicketRelation>> GetRelationsAsync(Guid ticketId, CancellationToken ct = default)
    {
        return await _db.TicketRelations
            .AsNoTracking()
            .Where(r => r.SourceTicketId == ticketId || r.TargetTicketId == ticketId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task RemoveRelationAsync(Guid relationId, CancellationToken ct = default)
    {
        var relation = await _db.TicketRelations.FindAsync([relationId], ct);
        if (relation is null) return;

        _db.TicketRelations.Remove(relation);
        await _db.SaveChangesAsync(ct);

        await _activityLogService.LogActivityAsync(
            relation.SourceTicketId,
            TicketActivityType.TicketRelationRemoved,
            null, null,
            $"relation:{(TicketRelationType)relation.RelationTypeValue}:{relation.TargetTicketId}",
            $"Relação removida com ticket #{relation.TargetTicketId}");

        _logger.LogInformation("Relation {Id} removed", relationId);
    }
}

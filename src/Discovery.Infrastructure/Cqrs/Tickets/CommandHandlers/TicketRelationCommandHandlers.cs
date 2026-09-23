using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;

public sealed class CreateTicketRelationCommandHandler(
    ITicketRelationRepository relationRepo,
    DiscoveryDbContext db,
    IActivityLogService activityLog
) : IRequestHandler<CreateTicketRelationCommand, Result<TicketRelationDto>>
{
    public async Task<Result<TicketRelationDto>> Handle(CreateTicketRelationCommand cmd, CancellationToken ct)
    {
        if (cmd.TicketId == cmd.TargetTicketId)
            return Result<TicketRelationDto>.Failure(
                Error.Validation("TargetTicketId", "Um chamado não pode ser relacionado a si mesmo."));

        if (await relationRepo.ExistsAsync(cmd.TicketId, cmd.TargetTicketId, cmd.RelationType, ct))
            return Result<TicketRelationDto>.Failure(
                Error.Validation("TargetTicketId", "Esta relação já existe."));

        // Bloqueia também o par inverso: evita A blocks B + B blocks A e
        // relações simétricas duplicadas (RelatesTo/Duplicate).
        if (await relationRepo.ExistsReverseAsync(cmd.TicketId, cmd.TargetTicketId, ct))
            return Result<TicketRelationDto>.Failure(
                Error.Validation("TargetTicketId", "Já existe uma relação entre estes chamados."));

        var found = await db.Tickets.AsNoTracking()
            .Where(t => (t.Id == cmd.TicketId || t.Id == cmd.TargetTicketId) && t.DeletedAt == null)
            .Select(t => t.Id)
            .ToListAsync(ct);
        if (found.Count != 2)
            return Result<TicketRelationDto>.Failure(
                Error.NotFound("Chamado de origem ou destino não encontrado."));

        var relation = new TicketRelation
        {
            Id = Guid.NewGuid(),
            SourceTicketId = cmd.TicketId,
            TargetTicketId = cmd.TargetTicketId,
            RelationTypeValue = (int)cmd.RelationType,
            CreatedBy = cmd.CreatedBy,
            CreatedAt = DateTime.UtcNow
        };

        await relationRepo.AddAsync(relation, ct);

        await activityLog.LogActivityAsync(
            cmd.TicketId, TicketActivityType.TicketRelationAdded, cmd.CreatedByUserId,
            null, cmd.TargetTicketId.ToString(), $"Relação {cmd.RelationType} criada.");

        return Result<TicketRelationDto>.Success(new TicketRelationDto(
            relation.Id, relation.SourceTicketId, relation.TargetTicketId,
            cmd.RelationType.ToString(), relation.CreatedBy, relation.CreatedAt, "source"));
    }
}

public sealed class DeleteTicketRelationCommandHandler(
    ITicketRelationRepository relationRepo,
    IActivityLogService activityLog
) : IRequestHandler<DeleteTicketRelationCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteTicketRelationCommand cmd, CancellationToken ct)
    {
        var relation = await relationRepo.GetByIdAsync(cmd.RelationId, ct);
        if (relation is null)
            return Result<VoidResult>.Failure(Error.NotFound("Relação não encontrada."));

        if (relation.SourceTicketId != cmd.TicketId && relation.TargetTicketId != cmd.TicketId)
            return Result<VoidResult>.Failure(Error.NotFound("Relação não pertence a este chamado."));

        var otherId = relation.SourceTicketId == cmd.TicketId ? relation.TargetTicketId : relation.SourceTicketId;

        await relationRepo.RemoveAsync(cmd.RelationId, ct);

        await activityLog.LogActivityAsync(
            cmd.TicketId, TicketActivityType.TicketRelationRemoved, cmd.ChangedByUserId,
            otherId.ToString(), null, "Relação removida.");

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

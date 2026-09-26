using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;

/// <summary>
/// Merge de tickets (consolida chamados de origem em um chamado alvo).
/// Operação transacional: move comentários, watchers, anexos, campos
/// personalizados, sessões remotas, automações, vínculos de KB e a timeline;
/// transita a origem para um estado final, registra TicketMergeRecord + relação
/// Duplicate + activity log TicketMerged e impede mesclagem duplicada.
/// </summary>
public sealed class MergeTicketsCommandHandler(
    IWorkflowRepository workflowRepo,
    DiscoveryDbContext db,
    ILogger<MergeTicketsCommandHandler> logger
) : IRequestHandler<MergeTicketsCommand, Result<MergeTicketsResult>>
{
    public async Task<Result<MergeTicketsResult>> Handle(MergeTicketsCommand cmd, CancellationToken ct)
    {
        var target = await db.Tickets
            .FirstOrDefaultAsync(t => t.Id == cmd.TargetTicketId && t.DeletedAt == null, ct);
        if (target is null)
            return Result<MergeTicketsResult>.Failure(Error.NotFound($"Target ticket {cmd.TargetTicketId} not found"));

        var sourceIds = (cmd.SourceTicketIds ?? Array.Empty<Guid>())
            .Where(id => id != cmd.TargetTicketId)
            .Distinct()
            .ToList();

        if (sourceIds.Count == 0)
            return Result<MergeTicketsResult>.Failure(
                Error.Validation("SourceTicketIds", "Informe ao menos um chamado de origem diferente do destino."));

        // Não remesclar a mesma origem (senão os comentários seriam duplicados).
        var alreadyMerged = await db.TicketMergeRecords
            .Where(r => sourceIds.Contains(r.SourceTicketId))
            .Select(r => r.SourceTicketId)
            .ToListAsync(ct);

        var pending = sourceIds.Except(alreadyMerged).ToList();
        if (pending.Count == 0)
            return Result<MergeTicketsResult>.Failure(
                Error.Validation("SourceTicketIds", "Todos os chamados de origem informados já foram mesclados."));

        var mergedBy = cmd.ChangedByUserId?.ToString();
        var mergedAt = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var mergedCount = 0;
        foreach (var sourceId in pending)
        {
            var source = await db.Tickets
                .FirstOrDefaultAsync(t => t.Id == sourceId && t.DeletedAt == null, ct);
            if (source is null)
            {
                logger.LogWarning("Source ticket {SourceId} not found, skipping merge", sourceId);
                continue;
            }

            await MoveSubResourcesAsync(source.Id, target.Id, ct);

            // Fecha a origem no primeiro estado final do seu workflow.
            var finalStateId = await ResolveFinalStateIdAsync(source, ct);
            if (finalStateId.HasValue)
                source.WorkflowStateId = finalStateId.Value;
            source.ClosedAt = DateTime.UtcNow;
            source.UpdatedAt = mergedAt;
            if (!source.Description.StartsWith("**MERGED", StringComparison.Ordinal))
                source.Description = $"**MERGED into {target.Id}**\n\n{source.Description}";

            db.TicketMergeRecords.Add(new TicketMergeRecord
            {
                Id = Guid.NewGuid(),
                SourceTicketId = source.Id,
                TargetTicketId = target.Id,
                MergedBy = mergedBy,
                Reason = cmd.Reason,
                MergedAt = mergedAt
            });

            db.TicketRelations.Add(new TicketRelation
            {
                Id = Guid.NewGuid(),
                SourceTicketId = source.Id,
                TargetTicketId = target.Id,
                RelationTypeValue = (int)TicketRelationType.Duplicate,
                CreatedBy = mergedBy,
                CreatedAt = mergedAt
            });

            db.TicketActivityLogs.Add(new TicketActivityLog
            {
                Id = Guid.NewGuid(),
                TicketId = target.Id,
                Type = TicketActivityType.TicketMerged,
                ChangedByUserId = cmd.ChangedByUserId,
                NewValue = source.Id.ToString(),
                Comment = "Chamado de origem mesclado neste chamado.",
                CreatedAt = mergedAt
            });
            db.TicketActivityLogs.Add(new TicketActivityLog
            {
                Id = Guid.NewGuid(),
                TicketId = source.Id,
                Type = TicketActivityType.TicketMerged,
                ChangedByUserId = cmd.ChangedByUserId,
                NewValue = target.Id.ToString(),
                Comment = "Chamado mesclado em outro (merged).",
                CreatedAt = mergedAt
            });

            mergedCount++;
            logger.LogInformation("Merged ticket {SourceId} into {TargetId}", source.Id, target.Id);
        }

        if (mergedCount == 0)
        {
            await tx.RollbackAsync(ct);
            return Result<MergeTicketsResult>.Failure(
                Error.Validation("SourceTicketIds", "No valid source tickets to merge"));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Result<MergeTicketsResult>.Success(new MergeTicketsResult(target.Id, mergedCount));
    }

    private async Task MoveSubResourcesAsync(Guid sourceId, Guid targetId, CancellationToken ct)
    {
        // Comentários
        foreach (var comment in await db.TicketComments.Where(c => c.TicketId == sourceId).ToListAsync(ct))
            comment.TicketId = targetId;

        // Watchers (sem duplicar o mesmo usuário no destino)
        var targetWatchers = await db.TicketWatchers
            .Where(w => w.TicketId == targetId)
            .Select(w => w.UserId)
            .ToListAsync(ct);
        foreach (var watcher in await db.TicketWatchers.Where(w => w.TicketId == sourceId).ToListAsync(ct))
        {
            if (targetWatchers.Contains(watcher.UserId))
                db.TicketWatchers.Remove(watcher);
            else
                watcher.TicketId = targetId;
        }

        // Anexos
        foreach (var attachment in await db.Attachments
            .Where(a => a.EntityType == "Ticket" && a.EntityId == sourceId && a.DeletedAt == null)
            .ToListAsync(ct))
            attachment.EntityId = targetId;

        // Campos personalizados
        foreach (var value in await db.CustomFieldValues
            .Where(v => v.ScopeType == CustomFieldScopeType.Ticket && v.EntityId == sourceId)
            .ToListAsync(ct))
            value.EntityId = targetId;

        // Respostas do questionário: move para o alvo. Em conflito de pergunta,
        // mantém a resposta do chamado de destino e descarta a da origem.
        var targetQuestionKeys = await db.TicketAnswers
            .Where(a => a.TicketId == targetId)
            .Select(a => a.QuestionKey)
            .ToListAsync(ct);
        foreach (var answer in await db.TicketAnswers.Where(a => a.TicketId == sourceId).ToListAsync(ct))
        {
            if (targetQuestionKeys.Contains(answer.QuestionKey, StringComparer.OrdinalIgnoreCase))
                db.TicketAnswers.Remove(answer);
            else
                answer.TicketId = targetId;
        }

        // Sessões remotas, automações e vínculos de KB
        foreach (var session in await db.TicketRemoteSessions.Where(s => s.TicketId == sourceId).ToListAsync(ct))
            session.TicketId = targetId;
        foreach (var link in await db.TicketAutomationLinks.Where(l => l.TicketId == sourceId).ToListAsync(ct))
            link.TicketId = targetId;
        foreach (var link in await db.TicketKnowledgeLinks.Where(l => l.TicketId == sourceId).ToListAsync(ct))
            link.TicketId = targetId;

        // Timeline
        foreach (var log in await db.TicketActivityLogs.Where(l => l.TicketId == sourceId).ToListAsync(ct))
            log.TicketId = targetId;
    }

    private async Task<Guid?> ResolveFinalStateIdAsync(Ticket source, CancellationToken ct)
    {
        var states = (await workflowRepo.GetStatesAsync(source.ClientId)).ToList();
        var current = states.FirstOrDefault(s => s.Id == source.WorkflowStateId);
        if (current?.IsFinal == true)
            return null;
        return states.Where(s => s.IsFinal).OrderBy(s => s.SortOrder).FirstOrDefault()?.Id;
    }
}

using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class TicketMergeService : ITicketMergeService
{
    private readonly DiscoveryDbContext _db;
    private readonly ITicketRepository _ticketRepo;
    private readonly ITicketWatcherRepository _watcherRepo;
    private readonly IActivityLogService _activityLogService;
    private readonly ILogger<TicketMergeService> _logger;

    public TicketMergeService(
        DiscoveryDbContext db,
        ITicketRepository ticketRepo,
        ITicketWatcherRepository watcherRepo,
        IActivityLogService activityLogService,
        ILogger<TicketMergeService> logger)
    {
        _db = db;
        _ticketRepo = ticketRepo;
        _watcherRepo = watcherRepo;
        _activityLogService = activityLogService;
        _logger = logger;
    }

    public async Task<TicketMergeRecord> MergeAsync(
        Guid sourceTicketId,
        Guid targetTicketId,
        string? mergedBy,
        string? reason,
        CancellationToken ct = default)
    {
        if (sourceTicketId == targetTicketId)
            throw new InvalidOperationException("Cannot merge a ticket into itself.");

        var source = await _ticketRepo.GetByIdAsync(sourceTicketId);
        var target = await _ticketRepo.GetByIdAsync(targetTicketId);

        if (source is null)
            throw new InvalidOperationException($"Source ticket {sourceTicketId} not found.");
        if (target is null)
            throw new InvalidOperationException($"Target ticket {targetTicketId} not found.");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // 1. Copiar comentários do source para o target
        var sourceComments = await _db.TicketComments
            .Where(c => c.TicketId == sourceTicketId)
            .ToListAsync(ct);

        foreach (var comment in sourceComments)
        {
            _db.TicketComments.Add(new TicketComment
            {
                Id = IdGenerator.NewId(),
                TicketId = targetTicketId,
                Author = $"[merged from #{sourceTicketId}] {comment.Author}",
                Content = comment.Content,
                IsInternal = comment.IsInternal,
                CreatedAt = comment.CreatedAt // preservar data original
            });
        }

        // 2. Reatribuir watchers do source para o target
        var sourceWatchers = await _watcherRepo.GetByTicketAsync(sourceTicketId);
        foreach (var watcher in sourceWatchers)
        {
            var alreadyWatching = await _db.TicketWatchers
                .AnyAsync(w => w.TicketId == targetTicketId && w.UserId == watcher.UserId, ct);
            if (!alreadyWatching)
            {
                _db.TicketWatchers.Add(new TicketWatcher
                {
                    Id = IdGenerator.NewId(),
                    TicketId = targetTicketId,
                    UserId = watcher.UserId,
                    AddedBy = mergedBy,
                    AddedAt = DateTime.UtcNow
                });
            }
        }

        // 3. Registrar merge record
        var mergeRecord = new TicketMergeRecord
        {
            Id = IdGenerator.NewId(),
            SourceTicketId = sourceTicketId,
            TargetTicketId = targetTicketId,
            MergedBy = mergedBy,
            Reason = reason,
            MergedAt = DateTime.UtcNow
        };
        _db.TicketMergeRecords.Add(mergeRecord);

        // 4. Fechar ticket source com atividade de merge
        var closedState = await _db.WorkflowStates
            .FirstOrDefaultAsync(s => s.IsFinal && (s.ClientId == source.ClientId || s.ClientId == null), ct);

        if (closedState is not null)
        {
            await _db.Tickets
                .Where(t => t.Id == sourceTicketId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.WorkflowStateId, closedState.Id)
                    .SetProperty(t => t.ClosedAt, DateTime.UtcNow)
                    .SetProperty(t => t.UpdatedAt, DateTime.UtcNow), ct);
        }

        // 5. Activity logs
        await _activityLogService.LogActivityAsync(
            sourceTicketId, TicketActivityType.TicketMerged,
            null, null, targetTicketId.ToString(),
            $"Ticket merged into #{targetTicketId}" + (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}"));

        await _activityLogService.LogActivityAsync(
            targetTicketId, TicketActivityType.TicketMerged,
            null, sourceTicketId.ToString(), null,
            $"Ticket #{sourceTicketId} was merged into this ticket" + (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}"));

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("Ticket {SourceId} merged into {TargetId} by {MergedBy}",
            sourceTicketId, targetTicketId, mergedBy);

        return mergeRecord;
    }
}

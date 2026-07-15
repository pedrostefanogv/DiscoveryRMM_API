using Discovery.Core.Entities;
using Discovery.Core.DTOs;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class TicketRepository : ITicketRepository
{
    private readonly DiscoveryDbContext _db;
    private readonly IAgentMessaging _messaging;
    private readonly ITicketKpiCacheService? _kpiCache;

    public TicketRepository(DiscoveryDbContext db, IAgentMessaging messaging, ITicketKpiCacheService? kpiCache = null)
    {
        _db = db;
        _messaging = messaging;
        _kpiCache = kpiCache;
    }

    public async Task<Ticket?> GetByIdAsync(Guid id)
    {
        return await _db.Tickets
            .AsNoTracking()
            .Where(ticket => ticket.DeletedAt == null)
            .SingleOrDefaultAsync(ticket => ticket.Id == id);
    }

    public async Task<IEnumerable<Ticket>> GetByClientIdAsync(Guid clientId, Guid? workflowStateId = null)
    {
        IQueryable<Ticket> query = _db.Tickets
            .AsNoTracking()
            .Where(ticket => ticket.ClientId == clientId && ticket.DeletedAt == null);

        if (workflowStateId.HasValue)
            query = query.Where(ticket => ticket.WorkflowStateId == workflowStateId.Value);

        return await query
            .OrderByDescending(ticket => ticket.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Ticket>> GetByAgentIdAsync(Guid agentId, Guid? workflowStateId = null)
    {
        IQueryable<Ticket> query = _db.Tickets
            .AsNoTracking()
            .Where(ticket => ticket.AgentId == agentId && ticket.DeletedAt == null);

        if (workflowStateId.HasValue)
            query = query.Where(ticket => ticket.WorkflowStateId == workflowStateId.Value);

        return await query
            .OrderByDescending(ticket => ticket.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Ticket>> GetAllAsync(TicketFilterQuery filter)
    {
        var query = BuildFilteredTicketQuery(filter);

        var safeLimit = Math.Clamp(filter.Limit, 1, 500);
        var safeOffset = Math.Max(0, filter.Offset);

        return await query
            .OrderByDescending(ticket => ticket.CreatedAt)
            .ThenByDescending(ticket => ticket.Id)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<Ticket>> GetAllPageAsync(TicketFilterQuery filter)
    {
        var query = BuildFilteredTicketQuery(filter);

        // Filtro de escopo ACL (mesmo padrão do LogRepository)
        if (!filter.HasGlobalAccess)
        {
            var allowedClientIds = (filter.AllowedClientIds ?? []).Distinct().ToArray();
            var allowedSiteIds = (filter.AllowedSiteIds ?? []).Distinct().ToArray();

            if (allowedClientIds.Length == 0 && allowedSiteIds.Length == 0)
                return [];

            query = query.Where(t =>
                allowedClientIds.Contains(t.ClientId) ||
                (t.SiteId.HasValue && allowedSiteIds.Contains(t.SiteId.Value)));
        }

        if (CursorPaginationHelper.TryDecodeCreatedAtCursor(filter.Cursor, out var cursorCreatedAtUtc, out var cursorId))
        {
            query = CursorPaginationHelper.ApplyCreatedAtCursor(
                query,
                cursorCreatedAtUtc,
                cursorId,
                ticket => ticket.CreatedAt,
                ticket => ticket.Id);
        }

        var safeLimit = Math.Clamp(filter.Limit, 1, 500);

        return await query
            .OrderByDescending(ticket => ticket.CreatedAt)
            .ThenByDescending(ticket => ticket.Id)
            .Take(safeLimit + 1)
            .ToListAsync();
    }

    private IQueryable<Ticket> BuildFilteredTicketQuery(TicketFilterQuery filter)
    {
        IQueryable<Ticket> query = _db.Tickets
            .AsNoTracking()
            .Where(ticket => ticket.DeletedAt == null);

        if (filter.ClientId.HasValue)
            query = query.Where(t => t.ClientId == filter.ClientId.Value);

        if (filter.SiteId.HasValue)
            query = query.Where(t => t.SiteId == filter.SiteId.Value);

        if (filter.AgentId.HasValue)
            query = query.Where(t => t.AgentId == filter.AgentId.Value);

        if (filter.DepartmentId.HasValue)
            query = query.Where(t => t.DepartmentId == filter.DepartmentId.Value);

        if (filter.WorkflowProfileId.HasValue)
            query = query.Where(t => t.WorkflowProfileId == filter.WorkflowProfileId.Value);

        if (filter.WorkflowStateId.HasValue)
            query = query.Where(t => t.WorkflowStateId == filter.WorkflowStateId.Value);

        if (filter.AssignedToUserId.HasValue)
            query = query.Where(t => t.AssignedToUserId == filter.AssignedToUserId.Value);

        if (filter.Priority.HasValue)
            query = query.Where(t => t.Priority == filter.Priority.Value);

        if (filter.SlaBreached.HasValue)
            query = query.Where(t => t.SlaBreached == filter.SlaBreached.Value);

        if (filter.IsClosed.HasValue)
            query = filter.IsClosed.Value
                ? query.Where(t => t.ClosedAt != null)
                : query.Where(t => t.ClosedAt == null);

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            var pattern = $"%{filter.Text.Trim()}%";
            query = query.Where(t =>
                EF.Functions.ILike(t.Title, pattern) ||
                EF.Functions.ILike(t.Description, pattern) ||
                (t.Category != null && EF.Functions.ILike(t.Category, pattern)));
        }

        return query;
    }

    public async Task<Ticket> CreateAsync(Ticket ticket)
    {
        ticket.Id = IdGenerator.NewId();
        ticket.CreatedAt = DateTime.UtcNow;
        ticket.UpdatedAt = DateTime.UtcNow;

         _db.Tickets.Add(ticket);
         await _db.SaveChangesAsync();
        await PublishDashboardEventAsync("TicketCreated", ticket);
        return ticket;
    }

    public async Task UpdateAsync(Ticket ticket)
    {
        var existingTicket = await _db.Tickets.SingleOrDefaultAsync(existing => existing.Id == ticket.Id);
        if (existingTicket is null)
            return;

        existingTicket.ClientId = ticket.ClientId;
        existingTicket.SiteId = ticket.SiteId;
        existingTicket.AgentId = ticket.AgentId;
        existingTicket.Title = ticket.Title;
        existingTicket.Description = ticket.Description;
        existingTicket.WorkflowStateId = ticket.WorkflowStateId;
        existingTicket.Priority = ticket.Priority;
        existingTicket.AssignedToUserId = ticket.AssignedToUserId;
        existingTicket.DepartmentId = ticket.DepartmentId;
        existingTicket.WorkflowProfileId = ticket.WorkflowProfileId;
        existingTicket.SlaExpiresAt = ticket.SlaExpiresAt;
        existingTicket.SlaBreached = ticket.SlaBreached;
        existingTicket.Category = ticket.Category;
        existingTicket.UpdatedAt = DateTime.UtcNow;
        existingTicket.ClosedAt = ticket.ClosedAt;

        await _db.SaveChangesAsync();
        await PublishDashboardEventAsync("TicketUpdated", existingTicket);
    }

    public async Task DeleteAsync(Guid id)
    {
        var now = DateTime.UtcNow;

        await _db.Tickets
            .Where(ticket => ticket.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(ticket => ticket.DeletedAt, _ => now)
                .SetProperty(ticket => ticket.UpdatedAt, _ => now));

        var deletedTicket = await _db.Tickets
            .AsNoTracking()
            .SingleOrDefaultAsync(ticket => ticket.Id == id);

        if (deletedTicket is not null)
            await PublishDashboardEventAsync("TicketDeleted", deletedTicket);
    }

    public async Task UpdateWorkflowStateAsync(Guid id, Guid workflowStateId, DateTime? closedAt = null)
    {
        var now = DateTime.UtcNow;

        await _db.Tickets
            .Where(ticket => ticket.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(ticket => ticket.WorkflowStateId, _ => workflowStateId)
                .SetProperty(ticket => ticket.ClosedAt, _ => closedAt)
                .SetProperty(ticket => ticket.UpdatedAt, _ => now));

        var updatedTicket = await _db.Tickets
            .AsNoTracking()
            .SingleOrDefaultAsync(ticket => ticket.Id == id);

        if (updatedTicket is not null)
            await PublishDashboardEventAsync("TicketWorkflowChanged", updatedTicket);
    }

    public async Task<IEnumerable<TicketComment>> GetCommentsAsync(Guid ticketId)
    {
        return await _db.TicketComments
            .AsNoTracking()
            .Where(comment => comment.TicketId == ticketId)
            .OrderBy(comment => comment.CreatedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<TicketComment>> GetCommentsPageAsync(Guid ticketId, string? cursor, int limit)
    {
        var query = _db.TicketComments
            .AsNoTracking()
            .Where(comment => comment.TicketId == ticketId);

        if (CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorCreatedAtUtc, out var cursorId))
        {
            query = CursorPaginationHelper.ApplyCreatedAtCursor(
                query,
                cursorCreatedAtUtc,
                cursorId,
                comment => comment.CreatedAt,
                comment => comment.Id);
        }

        var safeLimit = Math.Clamp(limit, 1, 200);

        return await query
            .OrderBy(comment => comment.CreatedAt)
            .ThenBy(comment => comment.Id)
            .Take(safeLimit + 1)
            .ToListAsync();
    }

    public async Task<TicketComment> AddCommentAsync(TicketComment comment)
    {
        comment.Id = IdGenerator.NewId();
        comment.CreatedAt = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync();

        _db.TicketComments.Add(comment);

        var now = DateTime.UtcNow;
        await _db.Tickets
            .Where(ticket => ticket.Id == comment.TicketId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(ticket => ticket.UpdatedAt, _ => now));

        await _db.SaveChangesAsync();

        await tx.CommitAsync();

        var ticket = await _db.Tickets
            .AsNoTracking()
            .SingleOrDefaultAsync(existingTicket => existingTicket.Id == comment.TicketId);

        if (ticket is not null)
            await PublishDashboardEventAsync("TicketCommentAdded", ticket);

        return comment;
    }

    public async Task<List<Ticket>> GetOpenTicketsWithSlaAsync()
    {
        return await _db.Tickets
            .AsNoTracking()
            .Where(ticket => !ticket.ClosedAt.HasValue && ticket.SlaExpiresAt.HasValue)
            .OrderBy(ticket => ticket.SlaExpiresAt)
            .ToListAsync();
    }

    public async Task UpdateSlaHoldAsync(Guid id, DateTime? slaHoldStartedAt, int slaPausedSeconds)
    {
        await _db.Tickets
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.SlaHoldStartedAt, _ => slaHoldStartedAt)
                .SetProperty(t => t.SlaPausedSeconds, _ => slaPausedSeconds)
                .SetProperty(t => t.UpdatedAt, _ => DateTime.UtcNow));
    }

    public async Task UpdateFirstRespondedAtAsync(Guid id, DateTime firstRespondedAt)
    {
        await _db.Tickets
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.FirstRespondedAt, _ => firstRespondedAt)
                .SetProperty(t => t.UpdatedAt, _ => DateTime.UtcNow));
    }

    public async Task<TicketKpiResult> GetKpiAsync(Guid? clientId, Guid? departmentId, DateTime? since)
    {
        var baseQuery = _db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null);

        if (clientId.HasValue)
            baseQuery = baseQuery.Where(t => t.ClientId == clientId.Value);
        if (departmentId.HasValue)
            baseQuery = baseQuery.Where(t => t.DepartmentId == departmentId.Value);
        if (since.HasValue)
            baseQuery = baseQuery.Where(t => t.CreatedAt >= since.Value);

        var now = DateTime.UtcNow;

        // ---- Agregações SQL (evita carregar todos os tickets em memória) ----
        var openQuery = baseQuery.Where(t => !t.ClosedAt.HasValue);
        var closedQuery = baseQuery.Where(t => t.ClosedAt.HasValue);

        // Executar contagens em paralelo (LOTE 1: apenas CountAsync)
        var totalOpenTask = openQuery.CountAsync();
        var totalClosedTask = closedQuery.CountAsync();
        var slaBreachedTask = openQuery.CountAsync(t => t.SlaBreached);
        var onHoldTask = openQuery.CountAsync(t => t.SlaHoldStartedAt.HasValue);

        // SLA warning: open, não breached, expires dentro de 2h
        var slaWarningThreshold = now.AddHours(2);
        var slaWarningTask = openQuery.CountAsync(t =>
            !t.SlaBreached && t.SlaExpiresAt.HasValue && t.SlaExpiresAt.Value <= slaWarningThreshold);

        // FRT achievements
        var frtQuery = baseQuery.Where(t => t.FirstRespondedAt.HasValue && t.SlaFirstResponseExpiresAt.HasValue);
        var frtAchievedCountTask = frtQuery.CountAsync(t => t.FirstRespondedAt!.Value <= t.SlaFirstResponseExpiresAt!.Value);
        var frtTotalCountTask = frtQuery.CountAsync();

        // Aguarda TODAS as CountAsync antes de iniciar qualquer ToListAsync
        await Task.WhenAll(
            totalOpenTask, totalClosedTask, slaBreachedTask, slaWarningTask, onHoldTask,
            frtAchievedCountTask, frtTotalCountTask);

        // LOTE 2: ToListAsync — sequencial para evitar concorrência no DbContext
        // (DbContext não é thread-safe; serializamos após o WhenAll acima)
        var closedDurations = await closedQuery
            .Select(t => new { t.CreatedAt, t.ClosedAt })
            .ToListAsync();
        var avgResolution = closedDurations.Count > 0
            ? closedDurations.Average(t => (t.ClosedAt!.Value - t.CreatedAt).TotalHours)
            : 0.0;

        // Avg age open
        var openDurations = await openQuery
            .Select(t => t.CreatedAt)
            .ToListAsync();
        var avgAgeOpen = openDurations.Count > 0
            ? openDurations.Average(t => (now - t).TotalHours)
            : 0.0;

        // LOTE 3: GroupBy assíncronos em paralelo
        var byAssigneeTask = openQuery
            .GroupBy(t => t.AssignedToUserId)
            .Select(g => new { AssignedToUserId = g.Key, Open = g.Count(), Breached = g.Count(t => t.SlaBreached) })
            .ToListAsync();

        var byDepartmentTask = openQuery
            .GroupBy(t => t.DepartmentId)
            .Select(g => new { DepartmentId = g.Key, Open = g.Count(), Breached = g.Count(t => t.SlaBreached) })
            .ToListAsync();

        await Task.WhenAll(byAssigneeTask, byDepartmentTask);

        var totalOpen = totalOpenTask.Result;
        var totalClosed = totalClosedTask.Result;
        var slaBreached = slaBreachedTask.Result;
        var slaWarning = slaWarningTask.Result;
        var onHold = onHoldTask.Result;

        var frtAchievedCount = frtAchievedCountTask.Result;
        var frtTotalCount = frtTotalCountTask.Result;
        var frtAchievementRate = frtTotalCount > 0 ? (frtAchievedCount / (double)frtTotalCount) * 100.0 : 0.0;

        var byAssignee = byAssigneeTask.Result
            .Select(g => new TicketKpiByAssignee(g.AssignedToUserId, g.Open, g.Breached))
            .ToList();

        var byDepartment = byDepartmentTask.Result
            .Select(g => new TicketKpiByDepartment(g.DepartmentId, g.Open, g.Breached))
            .ToList();

        return new TicketKpiResult(
            TotalOpen: totalOpen,
            TotalClosed: totalClosed,
            SlaBreached: slaBreached,
            SlaWarning: slaWarning,
            OnHold: onHold,
            FrtAchievementRate: Math.Round(frtAchievementRate, 2),
            AvgResolutionHours: Math.Round(avgResolution, 2),
            AvgAgeOpenHours: Math.Round(avgAgeOpen, 2),
            ByAssignee: byAssignee,
            ByDepartment: byDepartment
        );
    }

    private async Task PublishDashboardEventAsync(string eventType, Ticket ticket)
    {
        var publishTask = _messaging.PublishDashboardEventAsync(
            DashboardEventMessage.Create(
                eventType,
                new
                {
                    ticketId = ticket.Id,
                    ticket.ClientId,
                    ticket.SiteId,
                    ticket.AgentId,
                    closedAt = ticket.ClosedAt,
                    ticket.SlaBreached
                },
                ticket.ClientId,
                ticket.SiteId));

        // Invalidar cache KPI on-write
        var invalidateTask = _kpiCache?.InvalidateAsync(ticket.ClientId, CancellationToken.None) ?? Task.CompletedTask;

        await Task.WhenAll(publishTask, invalidateTask);
    }
}

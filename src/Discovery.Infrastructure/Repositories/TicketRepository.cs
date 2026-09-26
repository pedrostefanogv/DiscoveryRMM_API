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

    /// <summary>
    /// Transição de estado + ajuste de SLA-hold em UM único ExecuteUpdate:
    /// elimina a corrida entre transição e close/reabertura.
    /// </summary>
    public async Task UpdateWorkflowStateWithSlaHoldAsync(Guid id, Guid workflowStateId, DateTime? closedAt, DateTime? slaHoldStartedAt, int slaPausedSeconds)
    {
        await _db.Tickets
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.WorkflowStateId, workflowStateId)
                .SetProperty(t => t.ClosedAt, closedAt)
                .SetProperty(t => t.SlaHoldStartedAt, slaHoldStartedAt)
                .SetProperty(t => t.SlaPausedSeconds, slaPausedSeconds)
                .SetProperty(t => t.UpdatedAt, DateTime.UtcNow));
    }

    public async Task UpdateAsync(Ticket ticket)
    {
        var existingTicket = await _db.Tickets.SingleOrDefaultAsync(existing => existing.Id == ticket.Id);
        if (existingTicket is null)
            return;

        void Apply(Ticket source)
        {
            existingTicket.ClientId = source.ClientId;
            existingTicket.SiteId = source.SiteId;
            existingTicket.AgentId = source.AgentId;
            existingTicket.Title = source.Title;
            existingTicket.Description = source.Description;
            existingTicket.WorkflowStateId = source.WorkflowStateId;
            existingTicket.Priority = source.Priority;
            existingTicket.AssignedToUserId = source.AssignedToUserId;
            existingTicket.DepartmentId = source.DepartmentId;
            existingTicket.WorkflowProfileId = source.WorkflowProfileId;
            existingTicket.SlaExpiresAt = source.SlaExpiresAt;
            existingTicket.SlaFirstResponseExpiresAt = source.SlaFirstResponseExpiresAt;
            existingTicket.FirstRespondedAt = source.FirstRespondedAt;
            existingTicket.SlaPausedSeconds = source.SlaPausedSeconds;
            existingTicket.SlaHoldStartedAt = source.SlaHoldStartedAt;
            existingTicket.SlaBreached = source.SlaBreached;
            existingTicket.Rating = source.Rating;
            existingTicket.RatingFeedback = source.RatingFeedback;
            existingTicket.RatedAt = source.RatedAt;
            existingTicket.RatedBy = source.RatedBy;
            existingTicket.Category = source.Category;
            existingTicket.UpdatedAt = DateTime.UtcNow;
            existingTicket.ClosedAt = source.ClosedAt;
        }

        Apply(ticket);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Concorrência otimista (xmin): outro processo alterou o ticket entre a
            // leitura e a escrita. Recarrega e reaplica uma vez — a disputa passa a
            // ser DETECTADA em vez de virar lost update silencioso.
            await _db.Entry(existingTicket).ReloadAsync();
            Apply(ticket);
            await _db.SaveChangesAsync();
        }

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

        // Transaction is managed by the TransactionBehavior pipeline — no nested tx here.

        _db.TicketComments.Add(comment);

        var now = DateTime.UtcNow;
        await _db.Tickets
            .Where(ticket => ticket.Id == comment.TicketId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(ticket => ticket.UpdatedAt, _ => now));

        // SaveChangesAsync is called by TransactionBehavior after the handler returns,
        // but we need the comment persisted before publishing the dashboard event.
        await _db.SaveChangesAsync();

        var ticket = await _db.Tickets
            .AsNoTracking()
            .SingleOrDefaultAsync(existingTicket => existingTicket.Id == comment.TicketId);

        if (ticket is not null)
            await PublishDashboardEventAsync("TicketCommentAdded", ticket);

        return comment;
    }

    public async Task<List<Ticket>> GetOpenTicketsWithSlaAsync(int limit = 2000)
    {
        // Limitado: o job roda a cada 5 min e não deve carregar a tabela inteira.
        var effectiveLimit = Math.Clamp(limit, 1, 10_000);

        return await _db.Tickets
            .AsNoTracking()
            .Where(ticket => !ticket.ClosedAt.HasValue && ticket.SlaExpiresAt.HasValue)
            .OrderBy(ticket => ticket.SlaExpiresAt)
            .Take(effectiveLimit)
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

    public Task<TicketKpiResult> GetKpiAsync(Guid? clientId, Guid? departmentId, DateTime? since)
        => GetKpiAsync(new TicketFilterQuery(ClientId: clientId, DepartmentId: departmentId, Since: since));

    public async Task<TicketKpiResult> GetKpiAsync(TicketFilterQuery filter)
    {
        var baseQuery = BuildKpiBaseQuery(filter);

        var now = DateTime.UtcNow;

        // ---- Agregações SQL (evita carregar todos os tickets em memória) ----
        var openQuery = baseQuery.Where(t => !t.ClosedAt.HasValue);
        var closedQuery = baseQuery.Where(t => t.ClosedAt.HasValue);

        // SLA warning: open, não breached, expires dentro de 2h
        var slaWarningThreshold = now.AddHours(2);

        // FRT achievements
        var frtQuery = baseQuery.Where(t => t.FirstRespondedAt.HasValue && t.SlaFirstResponseExpiresAt.HasValue);

        // DbContext NÃO é thread-safe: as contagens são executadas sequencialmente.
        // Fazer Task.WhenAll de várias queries no mesmo contexto dispara
        // "A second operation was started on this context instance before a previous operation completed".
        var totalOpen = await openQuery.CountAsync();
        var totalClosed = await closedQuery.CountAsync();
        var slaBreached = await openQuery.CountAsync(t => t.SlaBreached);
        var onHold = await openQuery.CountAsync(t => t.SlaHoldStartedAt.HasValue);
        var slaWarning = await openQuery.CountAsync(t =>
            !t.SlaBreached && t.SlaExpiresAt.HasValue && t.SlaExpiresAt.Value <= slaWarningThreshold);
        var frtAchievedCount = await frtQuery.CountAsync(t => t.FirstRespondedAt!.Value <= t.SlaFirstResponseExpiresAt!.Value);
        var frtTotalCount = await frtQuery.CountAsync();

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

        // GroupBy sequenciais (mesmo motivo do bloco acima)
        var byAssignee = (await openQuery
            .GroupBy(t => t.AssignedToUserId)
            .Select(g => new { AssignedToUserId = g.Key, Open = g.Count(), Breached = g.Count(t => t.SlaBreached) })
            .ToListAsync())
            .Select(g => new TicketKpiByAssignee(g.AssignedToUserId, g.Open, g.Breached))
            .ToList();

        var byDepartment = (await openQuery
            .GroupBy(t => t.DepartmentId)
            .Select(g => new { DepartmentId = g.Key, Open = g.Count(), Breached = g.Count(t => t.SlaBreached) })
            .ToListAsync())
            .Select(g => new TicketKpiByDepartment(g.DepartmentId, g.Open, g.Breached))
            .ToList();

        var frtAchievementRate = frtTotalCount > 0 ? (frtAchievedCount / (double)frtTotalCount) * 100.0 : 0.0;

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

    /// <summary>
    /// Query base do KPI com os mesmos filtros da listagem + ACL (row-level security).
    /// </summary>
    private IQueryable<Ticket> BuildKpiBaseQuery(TicketFilterQuery filter)
    {
        var query = _db.Tickets.AsNoTracking().Where(t => t.DeletedAt == null);

        if (filter.ClientId.HasValue) query = query.Where(t => t.ClientId == filter.ClientId.Value);
        if (filter.SiteId.HasValue) query = query.Where(t => t.SiteId == filter.SiteId.Value);
        if (filter.AgentId.HasValue) query = query.Where(t => t.AgentId == filter.AgentId.Value);
        if (filter.DepartmentId.HasValue) query = query.Where(t => t.DepartmentId == filter.DepartmentId.Value);
        if (filter.WorkflowProfileId.HasValue) query = query.Where(t => t.WorkflowProfileId == filter.WorkflowProfileId.Value);
        if (filter.WorkflowStateId.HasValue) query = query.Where(t => t.WorkflowStateId == filter.WorkflowStateId.Value);
        if (filter.AssignedToUserId.HasValue) query = query.Where(t => t.AssignedToUserId == filter.AssignedToUserId.Value);
        if (filter.Priority.HasValue) query = query.Where(t => t.Priority == filter.Priority.Value);
        if (filter.SlaBreached.HasValue) query = query.Where(t => t.SlaBreached == filter.SlaBreached.Value);

        if (filter.IsClosed.HasValue)
            query = filter.IsClosed.Value
                ? query.Where(t => t.ClosedAt != null)
                : query.Where(t => t.ClosedAt == null);

        if (filter.Since.HasValue)
            query = query.Where(t => t.CreatedAt >= filter.Since.Value);

        // Consistência com a listagem: template de abertura e respostas do
        // questionário também recortam os KPIs.
        if (filter.TemplateId.HasValue) query = query.Where(t => t.TemplateId == filter.TemplateId.Value);
        query = query.WhereHasAnswer(_db.TicketAnswers, filter.AnswerKey, filter.AnswerValue, filter.AnswerMatch);

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            // Consistente com a listagem: inclui respostas do questionário.
            query = query.WhereMatchesText(_db.TicketAnswers, filter.Text.Trim());
        }

        if (!filter.HasGlobalAccess)
        {
            var allowedClientIds = (filter.AllowedClientIds ?? []).Distinct().ToArray();
            var allowedSiteIds = (filter.AllowedSiteIds ?? []).Distinct().ToArray();

            if (allowedClientIds.Length == 0 && allowedSiteIds.Length == 0)
                return query.Where(_ => false);

            query = query.Where(t =>
                allowedClientIds.Contains(t.ClientId) ||
                (t.SiteId.HasValue && allowedSiteIds.Contains(t.SiteId.Value)));
        }

        return query;
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

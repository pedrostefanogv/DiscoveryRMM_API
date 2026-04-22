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

    public TicketRepository(DiscoveryDbContext db, IAgentMessaging messaging)
    {
        _db = db;
        _messaging = messaging;
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

        var safeLimit = Math.Clamp(filter.Limit, 1, 500);
        var safeOffset = Math.Max(0, filter.Offset);

        return await query
            .OrderByDescending(ticket => ticket.CreatedAt)
            .Skip(safeOffset)
            .Take(safeLimit)
            .ToListAsync();
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

    public async Task<TicketComment> AddCommentAsync(TicketComment comment)
    {
        comment.Id = IdGenerator.NewId();
        comment.CreatedAt = DateTime.UtcNow;

        _db.TicketComments.Add(comment);

        var now = DateTime.UtcNow;
        await _db.Tickets
            .Where(ticket => ticket.Id == comment.TicketId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(ticket => ticket.UpdatedAt, _ => now));

        await _db.SaveChangesAsync();

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

        var allTickets = await baseQuery.ToListAsync();

        var open = allTickets.Where(t => !t.ClosedAt.HasValue).ToList();
        var closed = allTickets.Where(t => t.ClosedAt.HasValue).ToList();

        var totalOpen = open.Count;
        var totalClosed = closed.Count;
        var slaBreached = open.Count(t => t.SlaBreached);
        var slaWarning = open.Count(t =>
            !t.SlaBreached && t.SlaExpiresAt.HasValue &&
            (t.SlaExpiresAt.Value - now).TotalHours <= 2);
        var onHold = open.Count(t => t.SlaHoldStartedAt.HasValue);

        // FRT achievement rate: tickets com FirstRespondedAt <= SlaFirstResponseExpiresAt
        var withFrt = allTickets.Where(t => t.FirstRespondedAt.HasValue && t.SlaFirstResponseExpiresAt.HasValue).ToList();
        var frtAchievedCount = withFrt.Count(t => t.FirstRespondedAt!.Value <= t.SlaFirstResponseExpiresAt!.Value);
        var frtAchievementRate = withFrt.Count > 0 ? (frtAchievedCount / (double)withFrt.Count) * 100.0 : 0.0;

        // Avg resolution hours (closed only)
        var avgResolution = closed.Count > 0
            ? closed.Average(t => (t.ClosedAt!.Value - t.CreatedAt).TotalHours)
            : 0.0;

        // Avg age of open tickets
        var avgAgeOpen = open.Count > 0
            ? open.Average(t => (now - t.CreatedAt).TotalHours)
            : 0.0;

        // By assignee
        var byAssignee = open
            .GroupBy(t => t.AssignedToUserId)
            .Select(g => new TicketKpiByAssignee(
                g.Key,
                g.Count(),
                g.Count(t => t.SlaBreached)))
            .ToList();

        // By department
        var byDepartment = open
            .GroupBy(t => t.DepartmentId)
            .Select(g => new TicketKpiByDepartment(
                g.Key,
                g.Count(),
                g.Count(t => t.SlaBreached)))
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

    private Task PublishDashboardEventAsync(string eventType, Ticket ticket)
    {
        return _messaging.PublishDashboardEventAsync(
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
    }
}

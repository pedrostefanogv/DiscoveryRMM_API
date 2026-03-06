using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class TicketRepository : ITicketRepository
{
    private readonly MeduzaDbContext _db;

    public TicketRepository(MeduzaDbContext db) => _db = db;

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

    public async Task<IEnumerable<Ticket>> GetAllAsync(Guid? workflowStateId = null, int limit = 100, int offset = 0)
    {
        IQueryable<Ticket> query = _db.Tickets
            .AsNoTracking()
            .Where(ticket => ticket.DeletedAt == null);

        if (workflowStateId.HasValue)
            query = query.Where(ticket => ticket.WorkflowStateId == workflowStateId.Value);

        var safeLimit = Math.Clamp(limit, 1, 500);
        var safeOffset = Math.Max(0, offset);

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
    }

    public async Task DeleteAsync(Guid id)
    {
        var now = DateTime.UtcNow;

        await _db.Tickets
            .Where(ticket => ticket.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(ticket => ticket.DeletedAt, _ => now)
                .SetProperty(ticket => ticket.UpdatedAt, _ => now));
    }

    public async Task UpdateWorkflowStateAsync(Guid id, Guid workflowStateId)
    {
        var now = DateTime.UtcNow;

        await _db.Tickets
            .Where(ticket => ticket.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(ticket => ticket.WorkflowStateId, _ => workflowStateId)
                .SetProperty(ticket => ticket.UpdatedAt, _ => now));
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
}

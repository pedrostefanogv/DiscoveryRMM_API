using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class TicketRepository : ITicketRepository
{
    private readonly IDbConnectionFactory _db;

    public TicketRepository(IDbConnectionFactory db) => _db = db;

    public async Task<Ticket?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<Ticket>(
            """
            SELECT id, client_id AS ClientId, site_id AS SiteId, agent_id AS AgentId,
                   title, description, workflow_state_id AS WorkflowStateId,
                   priority, assigned_to AS AssignedTo,
                   category, created_at AS CreatedAt, updated_at AS UpdatedAt, closed_at AS ClosedAt
            FROM tickets WHERE id = @Id
            """, new { Id = id });
    }

    public async Task<IEnumerable<Ticket>> GetByClientIdAsync(Guid clientId, Guid? workflowStateId = null)
    {
        using var conn = _db.CreateConnection();
        var sql = """
            SELECT id, client_id AS ClientId, site_id AS SiteId, agent_id AS AgentId,
                   title, description, workflow_state_id AS WorkflowStateId,
                   priority, assigned_to AS AssignedTo,
                   category, created_at AS CreatedAt, updated_at AS UpdatedAt, closed_at AS ClosedAt
            FROM tickets WHERE client_id = @ClientId
            """;
        if (workflowStateId.HasValue) sql += " AND workflow_state_id = @WorkflowStateId";
        sql += " ORDER BY created_at DESC";
        return await conn.QueryAsync<Ticket>(sql, new { ClientId = clientId, WorkflowStateId = workflowStateId });
    }

    public async Task<IEnumerable<Ticket>> GetAllAsync(Guid? workflowStateId = null, int limit = 100, int offset = 0)
    {
        using var conn = _db.CreateConnection();
        var sql = """
            SELECT id, client_id AS ClientId, site_id AS SiteId, agent_id AS AgentId,
                   title, description, workflow_state_id AS WorkflowStateId,
                   priority, assigned_to AS AssignedTo,
                   category, created_at AS CreatedAt, updated_at AS UpdatedAt, closed_at AS ClosedAt
            FROM tickets
            """;
        if (workflowStateId.HasValue) sql += " WHERE workflow_state_id = @WorkflowStateId";
        sql += " ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset";
        return await conn.QueryAsync<Ticket>(sql, new { WorkflowStateId = workflowStateId, Limit = limit, Offset = offset });
    }

    public async Task<Ticket> CreateAsync(Ticket ticket)
    {
        ticket.Id = IdGenerator.NewId();
        ticket.CreatedAt = DateTime.UtcNow;
        ticket.UpdatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO tickets (id, client_id, site_id, agent_id, title, description,
                   workflow_state_id, priority, assigned_to, category, created_at, updated_at)
            VALUES (@Id, @ClientId, @SiteId, @AgentId, @Title, @Description,
                   @WorkflowStateId, @Priority, @AssignedTo, @Category, @CreatedAt, @UpdatedAt)
            """, ticket);
        return ticket;
    }

    public async Task UpdateAsync(Ticket ticket)
    {
        ticket.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE tickets SET client_id = @ClientId, site_id = @SiteId, agent_id = @AgentId,
                   title = @Title, description = @Description,
                   workflow_state_id = @WorkflowStateId, priority = @Priority,
                   assigned_to = @AssignedTo, category = @Category, updated_at = @UpdatedAt,
                   closed_at = @ClosedAt
            WHERE id = @Id
            """, ticket);
    }

    public async Task UpdateWorkflowStateAsync(Guid id, Guid workflowStateId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE tickets SET workflow_state_id = @WorkflowStateId, updated_at = @Now
            WHERE id = @Id
            """, new { Id = id, WorkflowStateId = workflowStateId, Now = DateTime.UtcNow });
    }

    public async Task<IEnumerable<TicketComment>> GetCommentsAsync(Guid ticketId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<TicketComment>(
            """
            SELECT id, ticket_id AS TicketId, author, content, is_internal AS IsInternal,
                   created_at AS CreatedAt
            FROM ticket_comments WHERE ticket_id = @TicketId ORDER BY created_at ASC
            """, new { TicketId = ticketId });
    }

    public async Task<TicketComment> AddCommentAsync(TicketComment comment)
    {
        comment.Id = IdGenerator.NewId();
        comment.CreatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO ticket_comments (id, ticket_id, author, content, is_internal, created_at)
            VALUES (@Id, @TicketId, @Author, @Content, @IsInternal, @CreatedAt)
            """, comment);

        await conn.ExecuteAsync(
            "UPDATE tickets SET updated_at = @Now WHERE id = @Id",
            new { Id = comment.TicketId, Now = DateTime.UtcNow });

        return comment;
    }
}

using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class EntityNoteRepository : IEntityNoteRepository
{
    private readonly IDbConnectionFactory _db;

    public EntityNoteRepository(IDbConnectionFactory db) => _db = db;

    private const string SelectColumns = """
        SELECT id,
               client_id AS ClientId,
               site_id AS SiteId,
               agent_id AS AgentId,
               content,
               author,
               is_pinned AS IsPinned,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt
        FROM entity_notes
        """;

    public async Task<EntityNote?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<EntityNote>(
            SelectColumns + " WHERE id = @Id",
            new { Id = id });
    }

    public async Task<IEnumerable<EntityNote>> GetByClientIdAsync(Guid clientId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<EntityNote>(
            SelectColumns + " WHERE client_id = @ClientId ORDER BY is_pinned DESC, created_at DESC",
            new { ClientId = clientId });
    }

    public async Task<IEnumerable<EntityNote>> GetBySiteIdAsync(Guid siteId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<EntityNote>(
            SelectColumns + " WHERE site_id = @SiteId ORDER BY is_pinned DESC, created_at DESC",
            new { SiteId = siteId });
    }

    public async Task<IEnumerable<EntityNote>> GetByAgentIdAsync(Guid agentId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<EntityNote>(
            SelectColumns + " WHERE agent_id = @AgentId ORDER BY is_pinned DESC, created_at DESC",
            new { AgentId = agentId });
    }

    public async Task<EntityNote> CreateAsync(EntityNote note)
    {
        note.Id = IdGenerator.NewId();
        note.CreatedAt = DateTime.UtcNow;
        note.UpdatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO entity_notes (
                id, client_id, site_id, agent_id,
                content, author, is_pinned,
                created_at, updated_at
            )
            VALUES (
                @Id, @ClientId, @SiteId, @AgentId,
                @Content, @Author, @IsPinned,
                @CreatedAt, @UpdatedAt
            )
            """,
            note);

        return note;
    }

    public async Task UpdateAsync(EntityNote note)
    {
        note.UpdatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE entity_notes
            SET content = @Content,
                author = @Author,
                is_pinned = @IsPinned,
                updated_at = @UpdatedAt
            WHERE id = @Id
            """,
            note);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM entity_notes WHERE id = @Id", new { Id = id });
    }
}

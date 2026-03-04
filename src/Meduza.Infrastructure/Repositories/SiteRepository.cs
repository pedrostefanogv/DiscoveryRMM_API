using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class SiteRepository : ISiteRepository
{
    private readonly IDbConnectionFactory _db;

    public SiteRepository(IDbConnectionFactory db) => _db = db;

    public async Task<Site?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<Site>(
            """
            SELECT id, client_id AS ClientId, name,
                   notes, is_active AS IsActive, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM sites WHERE id = @Id
            """, new { Id = id });
    }

    public async Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false)
    {
        using var conn = _db.CreateConnection();
        var sql = """
            SELECT id, client_id AS ClientId, name,
                   notes, is_active AS IsActive, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM sites WHERE client_id = @ClientId
            """;
        if (!includeInactive) sql += " AND is_active = true";
        sql += " ORDER BY name";
        return await conn.QueryAsync<Site>(sql, new { ClientId = clientId });
    }

    public async Task<Site> CreateAsync(Site site)
    {
        site.Id = IdGenerator.NewId();
        site.CreatedAt = DateTime.UtcNow;
        site.UpdatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO sites (id, client_id, name, notes, is_active, created_at, updated_at)
            VALUES (@Id, @ClientId, @Name, @Notes, @IsActive, @CreatedAt, @UpdatedAt)
            """, site);
        return site;
    }

    public async Task UpdateAsync(Site site)
    {
        site.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE sites SET client_id = @ClientId, name = @Name, notes = @Notes,
                   is_active = @IsActive, updated_at = @UpdatedAt
            WHERE id = @Id
            """, site);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("UPDATE sites SET is_active = false, updated_at = @Now WHERE id = @Id",
            new { Id = id, Now = DateTime.UtcNow });
    }
}

using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class ClientRepository : IClientRepository
{
    private readonly IDbConnectionFactory _db;

    public ClientRepository(IDbConnectionFactory db) => _db = db;

    public async Task<Client?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<Client>(
            """
            SELECT id, name, document, email, phone, notes, is_active AS IsActive,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM clients WHERE id = @Id
            """, new { Id = id });
    }

    public async Task<IEnumerable<Client>> GetAllAsync(bool includeInactive = false)
    {
        using var conn = _db.CreateConnection();
        var sql = """
            SELECT id, name, document, email, phone, notes, is_active AS IsActive,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM clients
            """;
        if (!includeInactive) sql += " WHERE is_active = true";
        sql += " ORDER BY name";
        return await conn.QueryAsync<Client>(sql);
    }

    public async Task<Client> CreateAsync(Client client)
    {
        client.Id = IdGenerator.NewId();
        client.CreatedAt = DateTime.UtcNow;
        client.UpdatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO clients (id, name, document, email, phone, notes, is_active, created_at, updated_at)
            VALUES (@Id, @Name, @Document, @Email, @Phone, @Notes, @IsActive, @CreatedAt, @UpdatedAt)
            """, client);
        return client;
    }

    public async Task UpdateAsync(Client client)
    {
        client.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE clients SET name = @Name, document = @Document, email = @Email,
                   phone = @Phone, notes = @Notes, is_active = @IsActive, updated_at = @UpdatedAt
            WHERE id = @Id
            """, client);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("UPDATE clients SET is_active = false, updated_at = @Now WHERE id = @Id",
            new { Id = id, Now = DateTime.UtcNow });
    }
}

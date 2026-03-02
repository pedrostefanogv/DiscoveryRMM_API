using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class AgentTokenRepository : IAgentTokenRepository
{
    private readonly IDbConnectionFactory _db;

    public AgentTokenRepository(IDbConnectionFactory db) => _db = db;

    public async Task<AgentToken?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<AgentToken>(
            """
            SELECT id, agent_id AS AgentId, token_hash AS TokenHash, token_prefix AS TokenPrefix,
                   description, expires_at AS ExpiresAt, created_at AS CreatedAt,
                   revoked_at AS RevokedAt, last_used_at AS LastUsedAt
            FROM agent_tokens WHERE id = @Id
            """, new { Id = id });
    }

    public async Task<AgentToken?> GetByTokenHashAsync(string tokenHash)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<AgentToken>(
            """
            SELECT id, agent_id AS AgentId, token_hash AS TokenHash, token_prefix AS TokenPrefix,
                   description, expires_at AS ExpiresAt, created_at AS CreatedAt,
                   revoked_at AS RevokedAt, last_used_at AS LastUsedAt
            FROM agent_tokens WHERE token_hash = @TokenHash
            """, new { TokenHash = tokenHash });
    }

    public async Task<IEnumerable<AgentToken>> GetByAgentIdAsync(Guid agentId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<AgentToken>(
            """
            SELECT id, agent_id AS AgentId, token_hash AS TokenHash, token_prefix AS TokenPrefix,
                   description, expires_at AS ExpiresAt, created_at AS CreatedAt,
                   revoked_at AS RevokedAt, last_used_at AS LastUsedAt
            FROM agent_tokens WHERE agent_id = @AgentId ORDER BY created_at DESC
            """, new { AgentId = agentId });
    }

    public async Task<AgentToken> CreateAsync(AgentToken token)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO agent_tokens (id, agent_id, token_hash, token_prefix, description, expires_at, created_at)
            VALUES (@Id, @AgentId, @TokenHash, @TokenPrefix, @Description, @ExpiresAt, @CreatedAt)
            """, token);
        return token;
    }

    public async Task UpdateLastUsedAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE agent_tokens SET last_used_at = @Now WHERE id = @Id",
            new { Id = id, Now = DateTime.UtcNow });
    }

    public async Task RevokeAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE agent_tokens SET revoked_at = @Now WHERE id = @Id AND revoked_at IS NULL",
            new { Id = id, Now = DateTime.UtcNow });
    }

    public async Task RevokeAllByAgentIdAsync(Guid agentId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE agent_tokens SET revoked_at = @Now WHERE agent_id = @AgentId AND revoked_at IS NULL",
            new { AgentId = agentId, Now = DateTime.UtcNow });
    }
}

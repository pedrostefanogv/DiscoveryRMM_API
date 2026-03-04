using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class DeployTokenRepository : IDeployTokenRepository
{
    private readonly IDbConnectionFactory _db;

    public DeployTokenRepository(IDbConnectionFactory db) => _db = db;

    public async Task<DeployToken?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<DeployToken>(
            """
            SELECT id, client_id AS ClientId, site_id AS SiteId, token_hash AS TokenHash, token_prefix AS TokenPrefix, description,
                   expires_at AS ExpiresAt, created_at AS CreatedAt, revoked_at AS RevokedAt,
                   last_used_at AS LastUsedAt, used_count AS UsedCount, max_uses AS MaxUses
            FROM deploy_tokens WHERE id = @Id
            """, new { Id = id });
    }

    public async Task<DeployToken?> GetByTokenHashAsync(string tokenHash)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<DeployToken>(
            """
            SELECT id, client_id AS ClientId, site_id AS SiteId, token_hash AS TokenHash, token_prefix AS TokenPrefix, description,
                   expires_at AS ExpiresAt, created_at AS CreatedAt, revoked_at AS RevokedAt,
                   last_used_at AS LastUsedAt, used_count AS UsedCount, max_uses AS MaxUses
            FROM deploy_tokens WHERE token_hash = @TokenHash
            """, new { TokenHash = tokenHash });
    }

    public async Task<DeployToken> CreateAsync(DeployToken token)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
             INSERT INTO deploy_tokens (id, client_id, site_id, token_hash, token_prefix, description, expires_at,
                 created_at, revoked_at, last_used_at, used_count, max_uses)
             VALUES (@Id, @ClientId, @SiteId, @TokenHash, @TokenPrefix, @Description, @ExpiresAt,
                 @CreatedAt, @RevokedAt, @LastUsedAt, @UsedCount, @MaxUses)
            """, token);
        return token;
    }

    public async Task<DeployToken?> TryUseByTokenHashAsync(string tokenHash, DateTime now)
    {
        using var conn = _db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            """
            UPDATE deploy_tokens
            SET used_count = used_count + 1, last_used_at = @Now
            WHERE token_hash = @TokenHash
              AND revoked_at IS NULL
              AND expires_at > @Now
              AND (max_uses IS NULL OR used_count < max_uses)
            """, new { TokenHash = tokenHash, Now = now });

        if (affected == 0)
            return null;

        return await conn.QuerySingleOrDefaultAsync<DeployToken>(
            """
            SELECT id, client_id AS ClientId, site_id AS SiteId, token_hash AS TokenHash, token_prefix AS TokenPrefix, description,
                   expires_at AS ExpiresAt, created_at AS CreatedAt, revoked_at AS RevokedAt,
                   last_used_at AS LastUsedAt, used_count AS UsedCount, max_uses AS MaxUses
            FROM deploy_tokens WHERE token_hash = @TokenHash
            """, new { TokenHash = tokenHash });
    }

    public async Task RevokeAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE deploy_tokens SET revoked_at = @Now WHERE id = @Id AND revoked_at IS NULL",
            new { Id = id, Now = DateTime.UtcNow });
    }
}

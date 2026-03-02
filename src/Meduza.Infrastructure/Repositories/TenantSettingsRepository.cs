using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class TenantSettingsRepository : ITenantSettingsRepository
{
    private readonly IDbConnectionFactory _db;

    public TenantSettingsRepository(IDbConnectionFactory db) => _db = db;

    public async Task<TenantSettings?> GetByClientIdAsync(Guid clientId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<TenantSettings>(
            """
            SELECT id, client_id AS ClientId, token_expiration_days AS TokenExpirationDays,
                   max_tokens_per_agent AS MaxTokensPerAgent,
                   agent_heartbeat_interval_seconds AS AgentHeartbeatIntervalSeconds,
                   agent_offline_threshold_seconds AS AgentOfflineThresholdSeconds,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM tenant_settings WHERE client_id = @ClientId
            """, new { ClientId = clientId });
    }

    public async Task<TenantSettings> UpsertAsync(TenantSettings settings)
    {
        settings.UpdatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM tenant_settings WHERE client_id = @ClientId",
            new { settings.ClientId });

        if (existing.HasValue)
        {
            settings.Id = existing.Value;
            await conn.ExecuteAsync(
                """
                UPDATE tenant_settings SET token_expiration_days = @TokenExpirationDays,
                       max_tokens_per_agent = @MaxTokensPerAgent,
                       agent_heartbeat_interval_seconds = @AgentHeartbeatIntervalSeconds,
                       agent_offline_threshold_seconds = @AgentOfflineThresholdSeconds,
                       updated_at = @UpdatedAt
                WHERE id = @Id
                """, settings);
        }
        else
        {
            settings.Id = IdGenerator.NewId();
            settings.CreatedAt = DateTime.UtcNow;
            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_settings (id, client_id, token_expiration_days, max_tokens_per_agent,
                       agent_heartbeat_interval_seconds, agent_offline_threshold_seconds, created_at, updated_at)
                VALUES (@Id, @ClientId, @TokenExpirationDays, @MaxTokensPerAgent,
                       @AgentHeartbeatIntervalSeconds, @AgentOfflineThresholdSeconds, @CreatedAt, @UpdatedAt)
                """, settings);
        }

        return settings;
    }
}

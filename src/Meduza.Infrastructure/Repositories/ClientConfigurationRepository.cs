using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class ClientConfigurationRepository : IClientConfigurationRepository
{
    private readonly IDbConnectionFactory _db;

    public ClientConfigurationRepository(IDbConnectionFactory db) => _db = db;

    private const string SelectColumns = """
        SELECT id, client_id AS ClientId,
               recovery_enabled AS RecoveryEnabled, discovery_enabled AS DiscoveryEnabled,
               p2p_files_enabled AS P2PFilesEnabled, support_enabled AS SupportEnabled,
               app_store_policy AS AppStorePolicy,
               ai_integration_settings_json AS AIIntegrationSettingsJson,
               inventory_interval_hours AS InventoryIntervalHours,
               auto_update_settings_json AS AutoUpdateSettingsJson,
               token_expiration_days AS TokenExpirationDays,
               max_tokens_per_agent AS MaxTokensPerAgent,
               agent_heartbeat_interval_seconds AS AgentHeartbeatIntervalSeconds,
               agent_offline_threshold_seconds AS AgentOfflineThresholdSeconds,
               created_at AS CreatedAt, updated_at AS UpdatedAt,
               created_by AS CreatedBy, updated_by AS UpdatedBy, version
        FROM client_configurations
        """;

    public async Task<ClientConfiguration?> GetByClientIdAsync(Guid clientId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<ClientConfiguration>(
            SelectColumns + " WHERE client_id = @ClientId", new { ClientId = clientId });
    }

    public async Task CreateAsync(ClientConfiguration config)
    {
        config.Id = IdGenerator.NewId();
        config.CreatedAt = DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO client_configurations (
                id, client_id,
                recovery_enabled, discovery_enabled, p2p_files_enabled, support_enabled,
                app_store_policy, ai_integration_settings_json,
                inventory_interval_hours, auto_update_settings_json,
                token_expiration_days, max_tokens_per_agent,
                agent_heartbeat_interval_seconds, agent_offline_threshold_seconds,
                created_at, updated_at, created_by, updated_by, version
            ) VALUES (
                @Id, @ClientId,
                @RecoveryEnabled, @DiscoveryEnabled, @P2PFilesEnabled, @SupportEnabled,
                @AppStorePolicy, @AIIntegrationSettingsJson,
                @InventoryIntervalHours, @AutoUpdateSettingsJson,
                @TokenExpirationDays, @MaxTokensPerAgent,
                @AgentHeartbeatIntervalSeconds, @AgentOfflineThresholdSeconds,
                @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy, @Version
            )
            """, config);
    }

    public async Task UpdateAsync(ClientConfiguration config)
    {
        config.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE client_configurations SET
                recovery_enabled = @RecoveryEnabled,
                discovery_enabled = @DiscoveryEnabled,
                p2p_files_enabled = @P2PFilesEnabled,
                support_enabled = @SupportEnabled,
                app_store_policy = @AppStorePolicy,
                ai_integration_settings_json = @AIIntegrationSettingsJson,
                inventory_interval_hours = @InventoryIntervalHours,
                auto_update_settings_json = @AutoUpdateSettingsJson,
                token_expiration_days = @TokenExpirationDays,
                max_tokens_per_agent = @MaxTokensPerAgent,
                agent_heartbeat_interval_seconds = @AgentHeartbeatIntervalSeconds,
                agent_offline_threshold_seconds = @AgentOfflineThresholdSeconds,
                updated_at = @UpdatedAt,
                updated_by = @UpdatedBy,
                version = @Version
            WHERE client_id = @ClientId
            """, config);
    }

    public async Task DeleteAsync(Guid clientId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM client_configurations WHERE client_id = @ClientId",
            new { ClientId = clientId });
    }

    public async Task<IEnumerable<ClientConfiguration>> GetAllAsync()
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<ClientConfiguration>(SelectColumns + " ORDER BY created_at");
    }
}

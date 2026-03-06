using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class ServerConfigurationRepository : IServerConfigurationRepository
{
    private readonly IDbConnectionFactory _db;

    public ServerConfigurationRepository(IDbConnectionFactory db) => _db = db;

    public async Task<ServerConfiguration?> GetAsync()
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<ServerConfiguration>(
            """
            SELECT id, recovery_enabled AS RecoveryEnabled, discovery_enabled AS DiscoveryEnabled,
                   p2p_files_enabled AS P2PFilesEnabled, support_enabled AS SupportEnabled,
                   knowledge_base_enabled AS KnowledgeBaseEnabled,
                   app_store_policy AS AppStorePolicy,
                   inventory_interval_hours AS InventoryIntervalHours,
                   auto_update_settings_json AS AutoUpdateSettingsJson,
                   token_expiration_days AS TokenExpirationDays,
                   max_tokens_per_agent AS MaxTokensPerAgent,
                   agent_heartbeat_interval_seconds AS AgentHeartbeatIntervalSeconds,
                   agent_offline_threshold_seconds AS AgentOfflineThresholdSeconds,
                   branding_settings_json AS BrandingSettingsJson,
                   ai_integration_settings_json AS AIIntegrationSettingsJson,
                   locked_fields_json AS LockedFieldsJson,
                   created_at AS CreatedAt, updated_at AS UpdatedAt,
                   created_by AS CreatedBy, updated_by AS UpdatedBy, version
            FROM server_configurations
            LIMIT 1
            """);
    }

    public async Task<ServerConfiguration> GetOrCreateDefaultAsync()
    {
        var existing = await GetAsync();
        if (existing is not null) return existing;

        var config = new ServerConfiguration { Id = IdGenerator.NewId() };
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO server_configurations (
                id, recovery_enabled, discovery_enabled, p2p_files_enabled, support_enabled,
                knowledge_base_enabled, app_store_policy, inventory_interval_hours,
                auto_update_settings_json, token_expiration_days, max_tokens_per_agent,
                agent_heartbeat_interval_seconds, agent_offline_threshold_seconds,
                branding_settings_json, ai_integration_settings_json,
                locked_fields_json,
                created_at, updated_at, created_by, updated_by, version
            ) VALUES (
                @Id, @RecoveryEnabled, @DiscoveryEnabled, @P2PFilesEnabled, @SupportEnabled,
                @KnowledgeBaseEnabled, @AppStorePolicy, @InventoryIntervalHours,
                @AutoUpdateSettingsJson, @TokenExpirationDays, @MaxTokensPerAgent,
                @AgentHeartbeatIntervalSeconds, @AgentOfflineThresholdSeconds,
                @BrandingSettingsJson, @AIIntegrationSettingsJson,
                @LockedFieldsJson,
                @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy, @Version
            )
            """, config);
        return config;
    }

    public async Task UpdateAsync(ServerConfiguration config)
    {
        config.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE server_configurations SET
                recovery_enabled = @RecoveryEnabled,
                discovery_enabled = @DiscoveryEnabled,
                p2p_files_enabled = @P2PFilesEnabled,
                support_enabled = @SupportEnabled,
                knowledge_base_enabled = @KnowledgeBaseEnabled,
                app_store_policy = @AppStorePolicy,
                inventory_interval_hours = @InventoryIntervalHours,
                auto_update_settings_json = @AutoUpdateSettingsJson,
                token_expiration_days = @TokenExpirationDays,
                max_tokens_per_agent = @MaxTokensPerAgent,
                agent_heartbeat_interval_seconds = @AgentHeartbeatIntervalSeconds,
                agent_offline_threshold_seconds = @AgentOfflineThresholdSeconds,
                branding_settings_json = @BrandingSettingsJson,
                ai_integration_settings_json = @AIIntegrationSettingsJson,
                locked_fields_json = @LockedFieldsJson,
                updated_at = @UpdatedAt,
                updated_by = @UpdatedBy,
                version = @Version
            WHERE id = @Id
            """, config);
    }

    public async Task<bool> ExistsAsync()
    {
        using var conn = _db.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM server_configurations");
        return count > 0;
    }
}

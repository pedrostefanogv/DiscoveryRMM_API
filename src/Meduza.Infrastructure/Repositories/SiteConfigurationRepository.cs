using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class SiteConfigurationRepository : ISiteConfigurationRepository
{
    private readonly IDbConnectionFactory _db;

    public SiteConfigurationRepository(IDbConnectionFactory db) => _db = db;

    private const string SelectColumns = """
        SELECT id, site_id AS SiteId, client_id AS ClientId,
               recovery_enabled AS RecoveryEnabled, discovery_enabled AS DiscoveryEnabled,
               p2p_files_enabled AS P2PFilesEnabled, support_enabled AS SupportEnabled,
               app_store_policy AS AppStorePolicy,
               ai_integration_settings_json AS AIIntegrationSettingsJson,
               inventory_interval_hours AS InventoryIntervalHours,
               auto_update_settings_json AS AutoUpdateSettingsJson,
               timezone, location, contact_person AS ContactPerson, contact_email AS ContactEmail,
               created_at AS CreatedAt, updated_at AS UpdatedAt,
               created_by AS CreatedBy, updated_by AS UpdatedBy, version
        FROM site_configurations
        """;

    public async Task<SiteConfiguration?> GetBySiteIdAsync(Guid siteId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<SiteConfiguration>(
            SelectColumns + " WHERE site_id = @SiteId", new { SiteId = siteId });
    }

    public async Task<IEnumerable<SiteConfiguration>> GetByClientIdAsync(Guid clientId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<SiteConfiguration>(
            SelectColumns + " WHERE client_id = @ClientId ORDER BY created_at", new { ClientId = clientId });
    }

    public async Task CreateAsync(SiteConfiguration config)
    {
        config.Id = IdGenerator.NewId();
        config.CreatedAt = DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO site_configurations (
                id, site_id, client_id,
                recovery_enabled, discovery_enabled, p2p_files_enabled, support_enabled,
                app_store_policy, ai_integration_settings_json,
                inventory_interval_hours, auto_update_settings_json,
                timezone, location, contact_person, contact_email,
                created_at, updated_at, created_by, updated_by, version
            ) VALUES (
                @Id, @SiteId, @ClientId,
                @RecoveryEnabled, @DiscoveryEnabled, @P2PFilesEnabled, @SupportEnabled,
                @AppStorePolicy, @AIIntegrationSettingsJson,
                @InventoryIntervalHours, @AutoUpdateSettingsJson,
                @Timezone, @Location, @ContactPerson, @ContactEmail,
                @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy, @Version
            )
            """, config);
    }

    public async Task UpdateAsync(SiteConfiguration config)
    {
        config.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE site_configurations SET
                recovery_enabled = @RecoveryEnabled,
                discovery_enabled = @DiscoveryEnabled,
                p2p_files_enabled = @P2PFilesEnabled,
                support_enabled = @SupportEnabled,
                app_store_policy = @AppStorePolicy,
                ai_integration_settings_json = @AIIntegrationSettingsJson,
                inventory_interval_hours = @InventoryIntervalHours,
                auto_update_settings_json = @AutoUpdateSettingsJson,
                timezone = @Timezone,
                location = @Location,
                contact_person = @ContactPerson,
                contact_email = @ContactEmail,
                updated_at = @UpdatedAt,
                updated_by = @UpdatedBy,
                version = @Version
            WHERE site_id = @SiteId
            """, config);
    }

    public async Task DeleteAsync(Guid siteId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM site_configurations WHERE site_id = @SiteId",
            new { SiteId = siteId });
    }
}

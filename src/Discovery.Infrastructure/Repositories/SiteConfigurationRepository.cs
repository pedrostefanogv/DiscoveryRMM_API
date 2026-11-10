using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class SiteConfigurationRepository : ISiteConfigurationRepository
{
    private readonly DiscoveryDbContext _db;

    public SiteConfigurationRepository(DiscoveryDbContext db) => _db = db;

    public async Task<SiteConfiguration?> GetBySiteIdAsync(Guid siteId)
    {
        return await _db.SiteConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(config => config.SiteId == siteId);
    }

    public async Task<IEnumerable<SiteConfiguration>> GetByClientIdAsync(Guid clientId)
    {
        return await _db.SiteConfigurations
            .AsNoTracking()
            .Where(config => config.ClientId == clientId)
            .OrderBy(config => config.CreatedAt)
            .ToListAsync();
    }

    public async Task CreateAsync(SiteConfiguration config)
    {
        config.Id = IdGenerator.NewId();
        config.CreatedAt = DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;
        _db.SiteConfigurations.Add(config);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(SiteConfiguration config)
    {
        config.UpdatedAt = DateTime.UtcNow;
        var existingConfig = await _db.SiteConfigurations.SingleOrDefaultAsync(existing => existing.SiteId == config.SiteId);
        if (existingConfig is null)
            return;

        existingConfig.ClientId = config.ClientId;
        existingConfig.RecoveryEnabled = config.RecoveryEnabled;
        existingConfig.DiscoveryEnabled = config.DiscoveryEnabled;
        existingConfig.P2PFilesEnabled = config.P2PFilesEnabled;
        existingConfig.SupportEnabled = config.SupportEnabled;
        existingConfig.ChatAIEnabled = config.ChatAIEnabled;
        existingConfig.KnowledgeBaseEnabled = config.KnowledgeBaseEnabled;
        existingConfig.UpdatesTabEnabled = config.UpdatesTabEnabled;
        existingConfig.AgentHomeTab = config.AgentHomeTab;
        existingConfig.ZeroTouchEnabled = config.ZeroTouchEnabled;
        existingConfig.KnowledgeEmbeddingEnabled = config.KnowledgeEmbeddingEnabled;
        // CFG-A: Modo observação da autorização por departamento (lido pelo ConfigurationResolver).
        existingConfig.DepartmentAuthShadowMode = config.DepartmentAuthShadowMode;
        // CFG-A: Notificações push (lido pelo ConfigurationResolver).
        existingConfig.PushEnabled = config.PushEnabled;
        // CFG-A: Sync do catálogo winget (lido pelo ConfigurationResolver).
        existingConfig.WingetCatalogEnabled = config.WingetCatalogEnabled;
        // CFG-A: Acesso remoto (lido pelo ConfigurationResolver).
        existingConfig.RemoteAccessEnabled = config.RemoteAccessEnabled;
        // CFG-A: Gravação de sessão remota (lido pelo ConfigurationResolver).
        existingConfig.RemoteAccessRecordingEnabled = config.RemoteAccessRecordingEnabled;
        // CFG-A: Remoção automática de gravações expiradas (lido pelo ConfigurationResolver).
        existingConfig.RemoteAccessRecordingAutoDeleteExpired = config.RemoteAccessRecordingAutoDeleteExpired;
        // CFG-A: Log automático (lido pelo ConfigurationResolver).
        existingConfig.AutomaticLoggingEnabled = config.AutomaticLoggingEnabled;
        existingConfig.AppStorePolicy = config.AppStorePolicy;
        existingConfig.AIIntegrationSettingsJson = config.AIIntegrationSettingsJson;
        existingConfig.InventoryIntervalHours = config.InventoryIntervalHours;
        existingConfig.AgentUpdatePolicyJson = config.AgentUpdatePolicyJson;
        existingConfig.AgentOnlineGraceSeconds = config.AgentOnlineGraceSeconds;
        existingConfig.Timezone = config.Timezone;
        existingConfig.Location = config.Location;
        existingConfig.ContactPerson = config.ContactPerson;
        existingConfig.ContactEmail = config.ContactEmail;
        existingConfig.LockedFieldsJson = config.LockedFieldsJson;
        existingConfig.UpdatedAt = config.UpdatedAt;
        existingConfig.UpdatedBy = config.UpdatedBy;
        existingConfig.Version = config.Version;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid siteId)
    {
        await _db.SiteConfigurations
            .Where(config => config.SiteId == siteId)
            .ExecuteDeleteAsync();
    }
}

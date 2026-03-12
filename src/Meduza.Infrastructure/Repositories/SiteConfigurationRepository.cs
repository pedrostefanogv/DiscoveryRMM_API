using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class SiteConfigurationRepository : ISiteConfigurationRepository
{
    private readonly MeduzaDbContext _db;

    public SiteConfigurationRepository(MeduzaDbContext db) => _db = db;

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
        existingConfig.AppStorePolicy = config.AppStorePolicy;
        existingConfig.AIIntegrationSettingsJson = config.AIIntegrationSettingsJson;
        existingConfig.InventoryIntervalHours = config.InventoryIntervalHours;
        existingConfig.AutoUpdateSettingsJson = config.AutoUpdateSettingsJson;
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

using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class ClientConfigurationRepository : IClientConfigurationRepository
{
    private readonly DiscoveryDbContext _db;

    public ClientConfigurationRepository(DiscoveryDbContext db) => _db = db;

    public async Task<ClientConfiguration?> GetByClientIdAsync(Guid clientId)
    {
        return await _db.ClientConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(config => config.ClientId == clientId);
    }

    public async Task CreateAsync(ClientConfiguration config)
    {
        config.Id = IdGenerator.NewId();
        config.CreatedAt = DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;
        _db.ClientConfigurations.Add(config);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(ClientConfiguration config)
    {
        config.UpdatedAt = DateTime.UtcNow;
        var existingConfig = await _db.ClientConfigurations.SingleOrDefaultAsync(existing => existing.ClientId == config.ClientId);
        if (existingConfig is null)
            return;

        existingConfig.RecoveryEnabled = config.RecoveryEnabled;
        existingConfig.DiscoveryEnabled = config.DiscoveryEnabled;
        existingConfig.P2PFilesEnabled = config.P2PFilesEnabled;
        existingConfig.CloudBootstrapEnabled = config.CloudBootstrapEnabled;
        existingConfig.SupportEnabled = config.SupportEnabled;
        existingConfig.MeshCentralGroupPolicyProfile = config.MeshCentralGroupPolicyProfile;
        existingConfig.ChatAIEnabled = config.ChatAIEnabled;
        existingConfig.KnowledgeBaseEnabled = config.KnowledgeBaseEnabled;
        existingConfig.AppStorePolicy = config.AppStorePolicy;
        existingConfig.AIIntegrationSettingsJson = config.AIIntegrationSettingsJson;
        existingConfig.InventoryIntervalHours = config.InventoryIntervalHours;
        existingConfig.AutoUpdateSettingsJson = config.AutoUpdateSettingsJson;
        existingConfig.AgentHeartbeatIntervalSeconds = config.AgentHeartbeatIntervalSeconds;
        existingConfig.AgentOnlineGraceSeconds = config.AgentOnlineGraceSeconds;
        existingConfig.LockedFieldsJson = config.LockedFieldsJson;
        existingConfig.UpdatedAt = config.UpdatedAt;
        existingConfig.UpdatedBy = config.UpdatedBy;
        existingConfig.Version = config.Version;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid clientId)
    {
        await _db.ClientConfigurations
            .Where(config => config.ClientId == clientId)
            .ExecuteDeleteAsync();
    }

    public async Task<IEnumerable<ClientConfiguration>> GetAllAsync()
    {
        return await _db.ClientConfigurations
            .AsNoTracking()
            .OrderBy(config => config.CreatedAt)
            .ToListAsync();
    }
}

using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Interface para repositório de configurações de servidor.
/// </summary>
public interface IServerConfigurationRepository
{
    Task<ServerConfiguration?> GetAsync();
    Task<ServerConfiguration> GetOrCreateDefaultAsync();
    Task UpdateAsync(ServerConfiguration config);
    Task<bool> ExistsAsync();
}

/// <summary>
/// Interface para repositório de configurações de cliente.
/// </summary>
public interface IClientConfigurationRepository
{
    Task<ClientConfiguration?> GetByClientIdAsync(Guid clientId);
    Task CreateAsync(ClientConfiguration config);
    Task UpdateAsync(ClientConfiguration config);
    Task DeleteAsync(Guid clientId);
    Task<IEnumerable<ClientConfiguration>> GetAllAsync();
}

/// <summary>
/// Interface para repositório de configurações de site.
/// </summary>
public interface ISiteConfigurationRepository
{
    Task<SiteConfiguration?> GetBySiteIdAsync(Guid siteId);
    Task<IEnumerable<SiteConfiguration>> GetByClientIdAsync(Guid clientId);
    Task CreateAsync(SiteConfiguration config);
    Task UpdateAsync(SiteConfiguration config);
    Task DeleteAsync(Guid siteId);
}

/// <summary>
/// Interface para repositório de auditoria de configurações.
/// </summary>
public interface IConfigurationAuditRepository
{
    Task CreateAsync(ConfigurationAudit audit);
    Task<IEnumerable<ConfigurationAudit>> GetByEntityAsync(string entityType, Guid entityId, int limit = 100);
    Task<IEnumerable<ConfigurationAudit>> GetRecentAsync(int days = 90, int limit = 1000);
    Task<IEnumerable<ConfigurationAudit>> GetByUserAsync(string username, int limit = 100);
}


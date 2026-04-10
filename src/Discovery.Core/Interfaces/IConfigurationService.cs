using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço para gerenciar configurações em todos os níveis (Server, Client, Site).
/// Inclui CRUD, validação e auditoria automática.
/// </summary>
public interface IConfigurationService
{
    // ============ Server Configuration ============
    
    Task<ServerConfiguration> GetServerConfigAsync();
    Task<ServerConfiguration> UpdateServerAsync(ServerConfiguration config, string? updatedBy = null);
    Task<ServerConfiguration> PatchServerAsync(Dictionary<string, object> updates, string? updatedBy = null);
    Task<ServerConfiguration> ResetServerAsync(string? resetBy = null);
    
    // ============ Client Configuration ============
    
    Task<ClientConfiguration?> GetClientConfigAsync(Guid clientId);
    Task<ClientConfiguration> CreateClientConfigAsync(Guid clientId, ClientConfiguration config, string? createdBy = null);
    Task<ClientConfiguration> UpdateClientAsync(Guid clientId, ClientConfiguration config, string? updatedBy = null);
    Task<ClientConfiguration> PatchClientAsync(Guid clientId, Dictionary<string, object> updates, string? updatedBy = null);
    Task DeleteClientConfigAsync(Guid clientId, string? deletedBy = null);
    Task ResetClientPropertyAsync(Guid clientId, string propertyName, string? resetBy = null);
    
    // ============ Site Configuration ============
    
    Task<SiteConfiguration?> GetSiteConfigAsync(Guid siteId);
    Task<SiteConfiguration> CreateSiteConfigAsync(Guid siteId, SiteConfiguration config, string? createdBy = null);
    Task<SiteConfiguration> UpdateSiteAsync(Guid siteId, SiteConfiguration config, string? updatedBy = null);
    Task<SiteConfiguration> PatchSiteAsync(Guid siteId, Dictionary<string, object> updates, string? updatedBy = null);
    Task DeleteSiteConfigAsync(Guid siteId, string? deletedBy = null);
    Task ResetSitePropertyAsync(Guid siteId, string propertyName, string? resetBy = null);
    
    // ============ Validação ============
    
    /// <summary>
    /// Valida uma configuração antes de salvar.
    /// </summary>
    Task<(bool IsValid, string[] Errors)> ValidateAsync(object config);
    
    /// <summary>
    /// Valida json de um objeto complexo (ex: AutoUpdateSettings).
    /// </summary>
    Task<(bool IsValid, string[] Errors)> ValidateJsonAsync(string objectType, string json);
}

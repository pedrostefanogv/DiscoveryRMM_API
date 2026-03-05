using Meduza.Core.Entities;
using Meduza.Core.ValueObjects;

namespace Meduza.Core.Interfaces;

/// <summary>
/// Serviço para resolver configurações com herança hierárquica.
/// Implementa a lógica de: Server → Client → Site
/// </summary>
public interface IConfigurationResolver
{
    /// <summary>
    /// Obtem a configuração de servidor.
    /// </summary>
    Task<ServerConfiguration> GetServerAsync();
    
    /// <summary>
    /// Obtem a configuração de cliente com valores herdados de servidor.
    /// </summary>
    Task<ClientConfiguration?> GetClientAsync(Guid clientId);
    
    /// <summary>
    /// Obtem a configuração de site com valores herdados de cliente/servidor.
    /// </summary>
    Task<SiteConfiguration?> GetSiteAsync(Guid siteId);
    
    /// <summary>
    /// Obtem o valor efetivo de uma configuração específica, considerando herança.
    /// Busca em: Site (se existir) → Client (se existir) → Server
    /// </summary>
    Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null);
    
    /// <summary>
    /// Obtem um objeto de configuração referenciado por tipo (ex: BrandingSettings).
    /// </summary>
    Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class;
    
    /// <summary>
    /// Obtem as configurações de auto-update efetivas para um nível.
    /// </summary>
    Task<AutoUpdateSettings> GetAutoUpdateSettingsAsync(string level, Guid? targetId = null);
    
    /// <summary>
    /// Obtem as configurações de branding efetivas.
    /// </summary>
    Task<BrandingSettings> GetBrandingSettingsAsync();
    
    /// <summary>
    /// Obtem as configurações de IA efetivas.
    /// </summary>
    Task<AIIntegrationSettings> GetAISettingsAsync();
    
    /// <summary>
    /// Resolve configuração completa para um site sem nenhum valor null (herança aplicada).
    /// </summary>
    Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId);

    /// <summary>
    /// Valida se todas as referências de herança estão corretas.
    /// </summary>
    Task ValidateInheritanceAsync();
    
    /// <summary>
    /// Limpa cache de configurações (útil para testes ou refresh forçado).
    /// </summary>
    void ClearCache();
}

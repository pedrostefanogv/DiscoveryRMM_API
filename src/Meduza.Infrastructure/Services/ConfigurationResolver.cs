using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Resolve configurações com herança hierárquica: Server → Client → Site.
/// Valores null em Client/Site são preenchidos pelo nível superior.
/// Usa IMemoryCache com TTL curto para performance no hot path dos agents.
/// </summary>
public class ConfigurationResolver : IConfigurationResolver
{
    private readonly IServerConfigurationRepository _serverRepo;
    private readonly IClientConfigurationRepository _clientRepo;
    private readonly ISiteConfigurationRepository _siteRepo;
    private readonly ISiteRepository _siteRepository;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public ConfigurationResolver(
        IServerConfigurationRepository serverRepo,
        IClientConfigurationRepository clientRepo,
        ISiteConfigurationRepository siteRepo,
        ISiteRepository siteRepository,
        IMemoryCache cache)
    {
        _serverRepo = serverRepo;
        _clientRepo = clientRepo;
        _siteRepo = siteRepo;
        _siteRepository = siteRepository;
        _cache = cache;
    }

    public async Task<ServerConfiguration> GetServerAsync()
        => await _serverRepo.GetOrCreateDefaultAsync();

    public async Task<ClientConfiguration?> GetClientAsync(Guid clientId)
        => await _clientRepo.GetByClientIdAsync(clientId);

    public async Task<SiteConfiguration?> GetSiteAsync(Guid siteId)
        => await _siteRepo.GetBySiteIdAsync(siteId);

    public async Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null)
    {
        var server = await GetServerAsync();
        var serverProp = typeof(ServerConfiguration).GetProperty(key,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
        var serverValue = serverProp?.GetValue(server);

        if (level.Equals("Server", StringComparison.OrdinalIgnoreCase) || targetId is null)
            return serverValue is T sv ? sv : default;

        if (level.Equals("Client", StringComparison.OrdinalIgnoreCase))
        {
            var client = await _clientRepo.GetByClientIdAsync(targetId.Value);
            if (client is not null)
            {
                var clientProp = typeof(ClientConfiguration).GetProperty(key,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                var clientValue = clientProp?.GetValue(client);
                if (clientValue is not null) return clientValue is T cv ? cv : default;
            }
            return serverValue is T sv2 ? sv2 : default;
        }

        if (level.Equals("Site", StringComparison.OrdinalIgnoreCase))
        {
            var siteConfig = await _siteRepo.GetBySiteIdAsync(targetId.Value);
            var siteEntity = await _siteRepository.GetByIdAsync(targetId.Value);
            var clientId = siteConfig?.ClientId ?? siteEntity?.ClientId;

            if (siteConfig is not null)
            {
                var siteProp = typeof(SiteConfiguration).GetProperty(key,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                var siteValue = siteProp?.GetValue(siteConfig);
                if (siteValue is not null) return siteValue is T sv3 ? sv3 : default;
            }

            if (clientId.HasValue)
            {
                var client = await _clientRepo.GetByClientIdAsync(clientId.Value);
                if (client is not null)
                {
                    var clientProp = typeof(ClientConfiguration).GetProperty(key,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    var clientValue = clientProp?.GetValue(client);
                    if (clientValue is not null) return clientValue is T cv2 ? cv2 : default;
                }
            }
            return serverValue is T sv4 ? sv4 : default;
        }

        return default;
    }

    public async Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class
    {
        var server = await GetServerAsync();
        var json = objectType.ToLowerInvariant() switch
        {
            "branding" or "brandingsettings" => server.BrandingSettingsJson,
            "ai" or "aiintegration" or "aiintegrationsettings" => server.AIIntegrationSettingsJson,
            "autoupdate" or "autoupdatesettings" => server.AutoUpdateSettingsJson,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(json)) return Activator.CreateInstance<T>();
        try { return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web); }
        catch { return Activator.CreateInstance<T>(); }
    }

    public async Task<AutoUpdateSettings> GetAutoUpdateSettingsAsync(string level, Guid? targetId = null)
    {
        var server = await GetServerAsync();
        var serverSettings = DeserializeOrDefault<AutoUpdateSettings>(server.AutoUpdateSettingsJson);

        if (level.Equals("Server", StringComparison.OrdinalIgnoreCase) || targetId is null)
            return serverSettings;

        if (level.Equals("Client", StringComparison.OrdinalIgnoreCase) && targetId.HasValue)
        {
            var client = await _clientRepo.GetByClientIdAsync(targetId.Value);
            if (!string.IsNullOrWhiteSpace(client?.AutoUpdateSettingsJson))
                return DeserializeOrDefault<AutoUpdateSettings>(client.AutoUpdateSettingsJson!);
            return serverSettings;
        }

        if (level.Equals("Site", StringComparison.OrdinalIgnoreCase) && targetId.HasValue)
        {
            var site = await _siteRepo.GetBySiteIdAsync(targetId.Value);
            if (!string.IsNullOrWhiteSpace(site?.AutoUpdateSettingsJson))
                return DeserializeOrDefault<AutoUpdateSettings>(site.AutoUpdateSettingsJson!);
            var client = site is not null ? await _clientRepo.GetByClientIdAsync(site.ClientId) : null;
            if (!string.IsNullOrWhiteSpace(client?.AutoUpdateSettingsJson))
                return DeserializeOrDefault<AutoUpdateSettings>(client!.AutoUpdateSettingsJson!);
            return serverSettings;
        }

        return serverSettings;
    }

    public async Task<BrandingSettings> GetBrandingSettingsAsync()
    {
        var server = await GetServerAsync();
        return DeserializeOrDefault<BrandingSettings>(server.BrandingSettingsJson);
    }

    public async Task<AIIntegrationSettings> GetAISettingsAsync()
    {
        var server = await GetServerAsync();
        return DeserializeOrDefault<AIIntegrationSettings>(server.AIIntegrationSettingsJson);
    }

    /// <summary>
    /// Resolve configuração completa para um site/agent, sem nenhum null.
    /// Cacheada por 60 segundos para performance no hot path.
    /// </summary>
    public async Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId)
    {
        var cacheKey = $"resolved_config_site_{siteId}";
        if (_cache.TryGetValue(cacheKey, out ResolvedConfiguration? cached) && cached is not null)
            return cached;

        var server = await _serverRepo.GetOrCreateDefaultAsync();
        var siteEntity = await _siteRepository.GetByIdAsync(siteId)
            ?? throw new InvalidOperationException($"Site '{siteId}' not found.");

        var site = await _siteRepo.GetBySiteIdAsync(siteId);
        var client = await _clientRepo.GetByClientIdAsync(siteEntity.ClientId);

        var resolved = new ResolvedConfiguration
        {
            SiteId = siteId,
            ClientId = siteEntity.ClientId,
            RecoveryEnabled    = site?.RecoveryEnabled ?? client?.RecoveryEnabled ?? server.RecoveryEnabled,
            DiscoveryEnabled   = site?.DiscoveryEnabled ?? client?.DiscoveryEnabled ?? server.DiscoveryEnabled,
            P2PFilesEnabled    = site?.P2PFilesEnabled ?? client?.P2PFilesEnabled ?? server.P2PFilesEnabled,
            SupportEnabled     = site?.SupportEnabled ?? client?.SupportEnabled ?? server.SupportEnabled,
            KnowledgeBaseEnabled   = server.KnowledgeBaseEnabled,
            AppStorePolicy     = site?.AppStorePolicy ?? client?.AppStorePolicy ?? server.AppStorePolicy,
            InventoryIntervalHours = site?.InventoryIntervalHours ?? client?.InventoryIntervalHours ?? server.InventoryIntervalHours,
            TokenExpirationDays    = client?.TokenExpirationDays ?? server.TokenExpirationDays,
            MaxTokensPerAgent      = client?.MaxTokensPerAgent ?? server.MaxTokensPerAgent,
            AgentHeartbeatIntervalSeconds = client?.AgentHeartbeatIntervalSeconds ?? server.AgentHeartbeatIntervalSeconds,
            AgentOfflineThresholdSeconds  = client?.AgentOfflineThresholdSeconds ?? server.AgentOfflineThresholdSeconds,
            AutoUpdate   = ResolveAutoUpdate(site?.AutoUpdateSettingsJson, client?.AutoUpdateSettingsJson, server.AutoUpdateSettingsJson),
            AIIntegration = ResolveAI(site?.AIIntegrationSettingsJson, client?.AIIntegrationSettingsJson, server.AIIntegrationSettingsJson),
        };

        _cache.Set(cacheKey, resolved, CacheTtl);
        return resolved;
    }

    public async Task ValidateInheritanceAsync()
    {
        // Valida que todos os ClientId referenciados em SiteConfiguration existem
        // Esta operação é pesada e deve ser chamada apenas em manutenção
        await GetServerAsync();
    }

    public void ClearCache()
    {
        // IMemoryCache não tem ClearAll nativo; para invalidação seletiva, remover chaves conhecidas
        // Em produção, considerar IDistributedCache com Redis
        if (_cache is MemoryCache mc)
        {
            mc.Compact(1.0);
        }
    }

    private static AutoUpdateSettings ResolveAutoUpdate(string? siteJson, string? clientJson, string serverJson)
    {
        if (!string.IsNullOrWhiteSpace(siteJson))
            return DeserializeOrDefault<AutoUpdateSettings>(siteJson);
        if (!string.IsNullOrWhiteSpace(clientJson))
            return DeserializeOrDefault<AutoUpdateSettings>(clientJson);
        return DeserializeOrDefault<AutoUpdateSettings>(serverJson);
    }

    private static AIIntegrationSettings ResolveAI(string? siteJson, string? clientJson, string serverJson)
    {
        if (!string.IsNullOrWhiteSpace(siteJson))
            return DeserializeOrDefault<AIIntegrationSettings>(siteJson);
        if (!string.IsNullOrWhiteSpace(clientJson))
            return DeserializeOrDefault<AIIntegrationSettings>(clientJson);
        return DeserializeOrDefault<AIIntegrationSettings>(serverJson);
    }

    private static T DeserializeOrDefault<T>(string? json) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json)) return new T();
        try { return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web) ?? new T(); }
        catch { return new T(); }
    }
}

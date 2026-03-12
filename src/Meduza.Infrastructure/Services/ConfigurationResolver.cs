using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Meduza.Core.Configuration;

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
        var globalLocks = GetBlockedFields(server.LockedFieldsJson);
        var clientLocks = GetBlockedFields(client?.LockedFieldsJson);
        var siteLocks = GetBlockedFields(site?.LockedFieldsJson);

        var blocked = new HashSet<string>(globalLocks, StringComparer.OrdinalIgnoreCase);
        blocked.UnionWith(clientLocks);
        blocked.UnionWith(siteLocks);

        var recovery = ResolveValue("DeviceRecoveryEnabled", blocked, site?.DeviceRecoveryEnabled, client?.DeviceRecoveryEnabled, server.DeviceRecoveryEnabled);
        var discovery = ResolveValue("AgentNetworkDiscoveryEnabled", blocked, site?.AgentNetworkDiscoveryEnabled, client?.AgentNetworkDiscoveryEnabled, server.AgentNetworkDiscoveryEnabled);
        var p2p = ResolveValue("P2PTransferEnabled", blocked, site?.P2PTransferEnabled, client?.P2PTransferEnabled, server.P2PTransferEnabled);
        var support = ResolveValue("RemoteSupportMeshCentralEnabled", blocked, site?.RemoteSupportMeshCentralEnabled, client?.RemoteSupportMeshCentralEnabled, server.RemoteSupportMeshCentralEnabled);
        var chatAi = ResolveValue("ChatAIEnabled", blocked, site?.ChatAIEnabled, client?.ChatAIEnabled, server.ChatAIEnabled);
        var knowledge = ResolveValue("KnowledgeBaseEnabled", blocked, site?.KnowledgeBaseEnabled, client?.KnowledgeBaseEnabled, server.KnowledgeBaseEnabled);
        var appStore = ResolveValue("AppStorePolicy", blocked, site?.AppStorePolicy, client?.AppStorePolicy, server.AppStorePolicy);
        var inventory = ResolveValue("InventoryIntervalHours", blocked, site?.InventoryIntervalHours, client?.InventoryIntervalHours, server.InventoryIntervalHours);
        var tokenExp = ResolveValue("TokenExpirationDays", blocked, (int?)null, client?.TokenExpirationDays, server.TokenExpirationDays);
        var maxTokens = ResolveValue("MaxTokensPerAgent", blocked, (int?)null, client?.MaxTokensPerAgent, server.MaxTokensPerAgent);
        var heartbeat = ResolveValue("AgentHeartbeatIntervalSeconds", blocked, (int?)null, client?.AgentHeartbeatIntervalSeconds, server.AgentHeartbeatIntervalSeconds);
        var offline = ResolveValue("AgentOfflineThresholdSeconds", blocked, (int?)null, client?.AgentOfflineThresholdSeconds, server.AgentOfflineThresholdSeconds);

        var autoUpdate = ResolveAutoUpdate(site?.AutoUpdateSettingsJson, client?.AutoUpdateSettingsJson, server.AutoUpdateSettingsJson);
        var autoUpdateSource = ResolveObjectSource("AutoUpdateSettingsJson", blocked, site?.AutoUpdateSettingsJson, client?.AutoUpdateSettingsJson);

        var ai = ResolveAI(site?.AIIntegrationSettingsJson, client?.AIIntegrationSettingsJson, server.AIIntegrationSettingsJson);
        var aiSource = ResolveObjectSource("AIIntegrationSettingsJson", blocked, site?.AIIntegrationSettingsJson, client?.AIIntegrationSettingsJson);

        var resolved = new ResolvedConfiguration
        {
            SiteId = siteId,
            ClientId = siteEntity.ClientId,
            RecoveryEnabled = recovery.Value,
            DiscoveryEnabled = discovery.Value,
            P2PFilesEnabled = p2p.Value,
            SupportEnabled = support.Value,
            ChatAIEnabled = chatAi.Value,
            KnowledgeBaseEnabled = knowledge.Value,
            AppStorePolicy = appStore.Value,
            InventoryIntervalHours = inventory.Value,
            TokenExpirationDays = tokenExp.Value,
            MaxTokensPerAgent = maxTokens.Value,
            AgentHeartbeatIntervalSeconds = heartbeat.Value,
            AgentOfflineThresholdSeconds = offline.Value,
            AutoUpdate = autoUpdate,
            AIIntegration = ai,
            BlockedFields = blocked.OrderBy(x => x).ToArray(),
        };

        resolved.Inheritance["RecoveryEnabled"] = (int)recovery.Source;
        resolved.Inheritance["DeviceRecoveryEnabled"] = (int)recovery.Source;
        resolved.Inheritance["DiscoveryEnabled"] = (int)discovery.Source;
        resolved.Inheritance["AgentNetworkDiscoveryEnabled"] = (int)discovery.Source;
        resolved.Inheritance["P2PFilesEnabled"] = (int)p2p.Source;
        resolved.Inheritance["P2PTransferEnabled"] = (int)p2p.Source;
        resolved.Inheritance["SupportEnabled"] = (int)support.Source;
        resolved.Inheritance["RemoteSupportMeshCentralEnabled"] = (int)support.Source;
        resolved.Inheritance["ChatAIEnabled"] = (int)chatAi.Source;
        resolved.Inheritance["KnowledgeBaseEnabled"] = (int)knowledge.Source;
        resolved.Inheritance["AppStorePolicy"] = (int)appStore.Source;
        resolved.Inheritance["InventoryIntervalHours"] = (int)inventory.Source;
        resolved.Inheritance["TokenExpirationDays"] = (int)tokenExp.Source;
        resolved.Inheritance["MaxTokensPerAgent"] = (int)maxTokens.Source;
        resolved.Inheritance["AgentHeartbeatIntervalSeconds"] = (int)heartbeat.Source;
        resolved.Inheritance["AgentOfflineThresholdSeconds"] = (int)offline.Source;
        resolved.Inheritance["AutoUpdate"] = (int)autoUpdateSource;
        resolved.Inheritance["AIIntegration"] = (int)aiSource;

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

    private static HashSet<string> GetBlockedFields(string? lockedFieldsJson)
    {
        if (string.IsNullOrWhiteSpace(lockedFieldsJson))
            return [];

        try
        {
            var fields = JsonSerializer.Deserialize<string[]>(lockedFieldsJson, JsonSerializerOptions.Web) ?? [];
            return ConfigurationFieldCatalog.NormalizeFieldSet(fields);
        }
        catch
        {
            return [];
        }
    }

    private static (T Value, ConfigurationPriorityType Source) ResolveValue<T>(
        string fieldName,
        HashSet<string> blockedFields,
        T? siteValue,
        T? clientValue,
        T serverValue)
        where T : struct
    {
        var isBlocked = blockedFields.Contains(fieldName);

        if (siteValue.HasValue)
            return (siteValue.Value, isBlocked ? ConfigurationPriorityType.Block : ConfigurationPriorityType.Site);

        if (clientValue.HasValue)
            return (clientValue.Value, isBlocked ? ConfigurationPriorityType.Block : ConfigurationPriorityType.Client);

        return (serverValue, isBlocked ? ConfigurationPriorityType.Block : ConfigurationPriorityType.Global);
    }

    private static ConfigurationPriorityType ResolveObjectSource(
        string fieldName,
        HashSet<string> blockedFields,
        string? siteJson,
        string? clientJson)
    {
        if (blockedFields.Contains(fieldName)) return ConfigurationPriorityType.Block;
        if (!string.IsNullOrWhiteSpace(siteJson)) return ConfigurationPriorityType.Site;
        if (!string.IsNullOrWhiteSpace(clientJson)) return ConfigurationPriorityType.Client;
        return ConfigurationPriorityType.Global;
    }
}

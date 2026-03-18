using System.Text.Json;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.Interfaces.Security;
using Meduza.Core.ValueObjects;
using Meduza.Core.Configuration;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Resolve configurações com herança hierárquica: Server → Client → Site.
/// Valores null em Client/Site são preenchidos pelo nível superior.
/// Usa Redis com TTL curto para performance no hot path dos agents.
/// </summary>
public class ConfigurationResolver : IConfigurationResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ResolvedSiteCachePrefix = "config:resolved:site:";

    private readonly IServerConfigurationRepository _serverRepo;
    private readonly IClientConfigurationRepository _clientRepo;
    private readonly ISiteConfigurationRepository _siteRepo;
    private readonly ISiteRepository _siteRepository;
    private readonly IRedisService _redisService;
    private readonly ISecretProtector _secretProtector;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public ConfigurationResolver(
        IServerConfigurationRepository serverRepo,
        IClientConfigurationRepository clientRepo,
        ISiteConfigurationRepository siteRepo,
        ISiteRepository siteRepository,
        IRedisService redisService,
        ISecretProtector secretProtector)
    {
        _serverRepo = serverRepo;
        _clientRepo = clientRepo;
        _siteRepo = siteRepo;
        _siteRepository = siteRepository;
        _redisService = redisService;
        _secretProtector = secretProtector;
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
        var normalizedType = objectType.ToLowerInvariant();
        var json = normalizedType switch
        {
            "branding" or "brandingsettings" => server.BrandingSettingsJson,
            "ai" or "aiintegration" or "aiintegrationsettings" => server.AIIntegrationSettingsJson,
            "autoupdate" or "autoupdatesettings" => server.AutoUpdateSettingsJson,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(json)) return Activator.CreateInstance<T>();
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web);
            if (value is AIIntegrationSettings ai)
                ai.ApiKey = _secretProtector.UnprotectOrSelf(ai.ApiKey);

            return value;
        }
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
        var settings = DeserializeOrDefault<AIIntegrationSettings>(server.AIIntegrationSettingsJson);
        settings.ApiKey = _secretProtector.UnprotectOrSelf(settings.ApiKey);
        return settings;
    }

    /// <summary>
    /// Resolve configuração completa para um site/agent, sem nenhum null.
    /// Cacheada por 5 minutos para performance no hot path.
    /// </summary>
    public async Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId)
    {
        var cacheKey = GetResolvedSiteCacheKey(siteId);
        var cached = await TryGetCachedResolvedConfigurationAsync(cacheKey);
        if (cached is not null)
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

        var recovery = ResolveValue("RecoveryEnabled", blocked, site?.RecoveryEnabled, client?.RecoveryEnabled, server.RecoveryEnabled);
        var discovery = ResolveValue("DiscoveryEnabled", blocked, site?.DiscoveryEnabled, client?.DiscoveryEnabled, server.DiscoveryEnabled);
        var p2p = ResolveValue("P2PFilesEnabled", blocked, site?.P2PFilesEnabled, client?.P2PFilesEnabled, server.P2PFilesEnabled);
        var support = ResolveValue("SupportEnabled", blocked, site?.SupportEnabled, client?.SupportEnabled, server.SupportEnabled);
        var meshPolicyProfile = ResolveStringValue(
            "MeshCentralGroupPolicyProfile",
            blocked,
            site?.MeshCentralGroupPolicyProfile,
            client?.MeshCentralGroupPolicyProfile,
            server.MeshCentralGroupPolicyProfile);
        var chatAi = ResolveValue("ChatAIEnabled", blocked, site?.ChatAIEnabled, client?.ChatAIEnabled, server.ChatAIEnabled);
        var knowledge = ResolveValue("KnowledgeBaseEnabled", blocked, site?.KnowledgeBaseEnabled, client?.KnowledgeBaseEnabled, server.KnowledgeBaseEnabled);
        var appStore = ResolveValue("AppStorePolicy", blocked, site?.AppStorePolicy, client?.AppStorePolicy, server.AppStorePolicy);
        var inventory = ResolveValue("InventoryIntervalHours", blocked, site?.InventoryIntervalHours, client?.InventoryIntervalHours, server.InventoryIntervalHours);
        var heartbeat = ResolveValue("AgentHeartbeatIntervalSeconds", blocked, (int?)null, client?.AgentHeartbeatIntervalSeconds, server.AgentHeartbeatIntervalSeconds);

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
            MeshCentralGroupPolicyProfile = meshPolicyProfile.Value,
            ChatAIEnabled = chatAi.Value,
            KnowledgeBaseEnabled = knowledge.Value,
            AppStorePolicy = appStore.Value,
            InventoryIntervalHours = inventory.Value,
            AgentHeartbeatIntervalSeconds = heartbeat.Value,
            AutoUpdate = autoUpdate,
            AIIntegration = ai,
            BlockedFields = blocked.OrderBy(x => x).ToArray(),
        };

        resolved.Inheritance["RecoveryEnabled"] = (int)recovery.Source;
        resolved.Inheritance["DiscoveryEnabled"] = (int)discovery.Source;
        resolved.Inheritance["P2PFilesEnabled"] = (int)p2p.Source;
        resolved.Inheritance["SupportEnabled"] = (int)support.Source;
        resolved.Inheritance["MeshCentralGroupPolicyProfile"] = (int)meshPolicyProfile.Source;
        resolved.Inheritance["ChatAIEnabled"] = (int)chatAi.Source;
        resolved.Inheritance["KnowledgeBaseEnabled"] = (int)knowledge.Source;
        resolved.Inheritance["AppStorePolicy"] = (int)appStore.Source;
        resolved.Inheritance["InventoryIntervalHours"] = (int)inventory.Source;
        resolved.Inheritance["AgentHeartbeatIntervalSeconds"] = (int)heartbeat.Source;
        resolved.Inheritance["AutoUpdate"] = (int)autoUpdateSource;
        resolved.Inheritance["AIIntegration"] = (int)aiSource;

        var payload = JsonSerializer.Serialize(resolved, JsonOptions);
        await _redisService.SetAsync(cacheKey, payload, (int)CacheTtl.TotalSeconds);
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
        _redisService.DeleteByPrefixAsync(ResolvedSiteCachePrefix).GetAwaiter().GetResult();
    }

    private static AutoUpdateSettings ResolveAutoUpdate(string? siteJson, string? clientJson, string serverJson)
    {
        if (!string.IsNullOrWhiteSpace(siteJson))
            return DeserializeOrDefault<AutoUpdateSettings>(siteJson);
        if (!string.IsNullOrWhiteSpace(clientJson))
            return DeserializeOrDefault<AutoUpdateSettings>(clientJson);
        return DeserializeOrDefault<AutoUpdateSettings>(serverJson);
    }

    private AIIntegrationSettings ResolveAI(string? siteJson, string? clientJson, string serverJson)
    {
        // Sempre começa com a base global (servidor) — fonte de verdade para campos globais
        // (ApiKey, EmbeddingModel, Provider, EmbeddingEnabled, etc.)
        var result = DeserializeOrDefault<AIIntegrationSettings>(serverJson);
        result.ApiKey = _secretProtector.UnprotectOrSelf(result.ApiKey);

        // Aplica override de client: só campos presentes em AIIntegrationSettingsOverride
        // são sobrescritos; campos globais (ApiKey, EmbeddingModel, etc.) são ignorados por design.
        if (!string.IsNullOrWhiteSpace(clientJson))
            ApplyAiOverride(result, DeserializeOrDefault<AIIntegrationSettingsOverride>(clientJson));

        // Aplica override de site: tem precedência sobre client e servidor
        if (!string.IsNullOrWhiteSpace(siteJson))
            ApplyAiOverride(result, DeserializeOrDefault<AIIntegrationSettingsOverride>(siteJson));

        return result;
    }

    /// <summary>
    /// Aplica campos não-null de um AIIntegrationSettingsOverride sobre o objeto base do servidor.
    /// Campos ausentes (null) significam "herdar do nível superior".
    /// </summary>
    private static void ApplyAiOverride(AIIntegrationSettings target, AIIntegrationSettingsOverride ov)
    {
        if (ov.Enabled.HasValue) target.Enabled = ov.Enabled.Value;
        if (ov.ChatAIEnabled.HasValue) target.ChatAIEnabled = ov.ChatAIEnabled.Value;
        if (ov.KnowledgeBaseEnabled.HasValue) target.KnowledgeBaseEnabled = ov.KnowledgeBaseEnabled.Value;
        if (!string.IsNullOrWhiteSpace(ov.ChatModel)) target.ChatModel = ov.ChatModel;
        if (!string.IsNullOrWhiteSpace(ov.PromptTemplate)) target.PromptTemplate = ov.PromptTemplate;
        if (ov.Temperature.HasValue) target.Temperature = ov.Temperature.Value;
        if (ov.MaxTokensPerRequest.HasValue) target.MaxTokensPerRequest = ov.MaxTokensPerRequest.Value;
        if (ov.MaxHistoryMessages.HasValue) target.MaxHistoryMessages = ov.MaxHistoryMessages.Value;
        if (ov.MaxKbContextTokens.HasValue) target.MaxKbContextTokens = ov.MaxKbContextTokens.Value;
        if (ov.MaxKbChunks.HasValue) target.MaxKbChunks = ov.MaxKbChunks.Value;
        if (ov.MinSimilarityScore.HasValue) target.MinSimilarityScore = ov.MinSimilarityScore.Value;
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

    private static (string Value, ConfigurationPriorityType Source) ResolveStringValue(
        string fieldName,
        HashSet<string> blockedFields,
        string? siteValue,
        string? clientValue,
        string serverValue)
    {
        var isBlocked = blockedFields.Contains(fieldName);

        if (!string.IsNullOrWhiteSpace(siteValue))
            return (siteValue, isBlocked ? ConfigurationPriorityType.Block : ConfigurationPriorityType.Site);

        if (!string.IsNullOrWhiteSpace(clientValue))
            return (clientValue, isBlocked ? ConfigurationPriorityType.Block : ConfigurationPriorityType.Client);

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

    private async Task<ResolvedConfiguration?> TryGetCachedResolvedConfigurationAsync(string cacheKey)
    {
        var cached = await _redisService.GetAsync(cacheKey);
        if (string.IsNullOrWhiteSpace(cached))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ResolvedConfiguration>(cached, JsonOptions);
        }
        catch (JsonException)
        {
            await _redisService.DeleteAsync(cacheKey);
            return null;
        }
    }

    private static string GetResolvedSiteCacheKey(Guid siteId) => $"{ResolvedSiteCachePrefix}{siteId:N}";
}

using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

public class ConfigurationService : IConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ServerConfigCacheKey = "config:server";
    private static readonly int ServerConfigCacheTtlSeconds = (int)TimeSpan.FromMinutes(5).TotalSeconds;

    private readonly IServerConfigurationRepository _serverRepo;
    private readonly IClientConfigurationRepository _clientRepo;
    private readonly ISiteConfigurationRepository _siteRepo;
    private readonly ISiteRepository _siteRepository;
    private readonly IConfigurationAuditService _audit;
    private readonly IConfigurationResolver _resolver;
    private readonly ISecretProtector _secretProtector;
    private readonly IRedisService _redisService;
    private readonly INatsConnectionValidator _natsConnectionValidator;

    public ConfigurationService(
        IServerConfigurationRepository serverRepo,
        IClientConfigurationRepository clientRepo,
        ISiteConfigurationRepository siteRepo,
        ISiteRepository siteRepository,
        IConfigurationAuditService audit,
        IConfigurationResolver resolver,
        ISecretProtector secretProtector,
        IRedisService redisService,
        INatsConnectionValidator natsConnectionValidator)
    {
        _serverRepo = serverRepo;
        _clientRepo = clientRepo;
        _siteRepo = siteRepo;
        _siteRepository = siteRepository;
        _audit = audit;
        _resolver = resolver;
        _secretProtector = secretProtector;
        _redisService = redisService;
        _natsConnectionValidator = natsConnectionValidator;
    }

    // ============ Server ============

    public async Task<ServerConfiguration> GetServerConfigAsync()
    {
        var cached = await _redisService.GetAsync(ServerConfigCacheKey);
        if (cached is not null)
        {
            var deserialized = JsonSerializer.Deserialize<ServerConfiguration>(cached, JsonOptions);
            if (deserialized is not null)
                return deserialized;
        }

        var config = await _serverRepo.GetOrCreateDefaultAsync();
        await _redisService.SetAsync(ServerConfigCacheKey, JsonSerializer.Serialize(config, JsonOptions), ServerConfigCacheTtlSeconds);
        return config;
    }

    public async Task<ServerConfiguration> UpdateServerAsync(ServerConfiguration config, string? updatedBy = null)
    {
        var existing = await _serverRepo.GetOrCreateDefaultAsync();
        var previousLocks = ParseLockedFields(existing.LockedFieldsJson);

        config.Id = existing.Id;
        config.CreatedAt = existing.CreatedAt;
        config.CreatedBy = existing.CreatedBy;
        config.Version = existing.Version + 1;
        config.UpdatedBy = updatedBy;
        ProtectSensitiveData(config);
        await _serverRepo.UpdateAsync(config);

        var addedLocks = GetAddedLocks(previousLocks, ParseLockedFields(config.LockedFieldsJson));
        if (addedLocks.Count > 0)
            await ApplyServerLockCascadeAsync(addedLocks, updatedBy);

        _resolver.ClearCache();
        await _redisService.DeleteAsync(ServerConfigCacheKey);
        await _audit.LogChangeAsync("Server", config.Id, "*", null, null, "Full update", updatedBy);
        return config;
    }

    public async Task<ServerConfiguration> PatchServerAsync(Dictionary<string, object> updates, string? updatedBy = null)
    {
        var config = await _serverRepo.GetOrCreateDefaultAsync();
        var previousLocks = ParseLockedFields(config.LockedFieldsJson);

        foreach (var (key, value) in updates)
        {
            var prop = typeof(ServerConfiguration).GetProperty(key,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop is null || !prop.CanWrite) continue;
            var oldValue = prop.GetValue(config)?.ToString();
            var converted = ConvertToPropertyValue(value, prop.PropertyType);
            converted = ProtectPatchValue(key, converted);
            prop.SetValue(config, converted);
            await _audit.LogChangeAsync("Server", config.Id, key, MaskForAudit(key, oldValue), MaskForAudit(key, converted?.ToString()), null, updatedBy);
        }
        config.Version++;
        config.UpdatedBy = updatedBy;
        await _serverRepo.UpdateAsync(config);

        var addedLocks = GetAddedLocks(previousLocks, ParseLockedFields(config.LockedFieldsJson));
        if (addedLocks.Count > 0)
            await ApplyServerLockCascadeAsync(addedLocks, updatedBy);

        _resolver.ClearCache();
        await _redisService.DeleteAsync(ServerConfigCacheKey);
        return config;
    }

    public async Task<ServerConfiguration> ResetServerAsync(string? resetBy = null)
    {
        var existing = await _serverRepo.GetOrCreateDefaultAsync();
        var reset = new ServerConfiguration
        {
            Id = existing.Id,
            CreatedAt = existing.CreatedAt,
            CreatedBy = existing.CreatedBy,
            UpdatedBy = resetBy,
            Version = existing.Version + 1
        };
        await _serverRepo.UpdateAsync(reset);
        _resolver.ClearCache();
        await _redisService.DeleteAsync(ServerConfigCacheKey);
        await _audit.LogChangeAsync("Server", existing.Id, "*", null, null, "Reset to defaults", resetBy);
        return reset;
    }

    // ============ Client ============

    public async Task<ClientConfiguration?> GetClientConfigAsync(Guid clientId)
        => await _clientRepo.GetByClientIdAsync(clientId);

    public async Task<ClientConfiguration> CreateClientConfigAsync(Guid clientId, ClientConfiguration config, string? createdBy = null)
    {
        var blockedFields = await GetGlobalBlockedFieldsAsync();
        EnsureNoBlockedOverrides(config, blockedFields, "Client");

        NormalizeNullableBooleanDefaults(config);

        config.ClientId = clientId;
        config.CreatedBy = createdBy;
        config.UpdatedBy = createdBy;
        await _clientRepo.CreateAsync(config);
        _resolver.ClearCache();
        await _audit.LogChangeAsync("Client", config.Id, "*", null, null, "Created", createdBy);
        return config;
    }

    public async Task<ClientConfiguration> UpdateClientAsync(Guid clientId, ClientConfiguration config, string? updatedBy = null)
    {
        var blockedFields = await GetGlobalBlockedFieldsAsync();
        EnsureNoBlockedOverrides(config, blockedFields, "Client");

        NormalizeNullableBooleanDefaults(config);

        var existing = await _clientRepo.GetByClientIdAsync(clientId);
        if (existing is null)
            return await CreateClientConfigAsync(clientId, config, updatedBy);

        var previousLocks = ParseLockedFields(existing.LockedFieldsJson);

        config.Id = existing.Id;
        config.ClientId = clientId;
        config.CreatedAt = existing.CreatedAt;
        config.CreatedBy = existing.CreatedBy;
        config.Version = existing.Version + 1;
        config.UpdatedBy = updatedBy;
        ProtectSensitiveData(config);
        await _clientRepo.UpdateAsync(config);

        var addedLocks = GetAddedLocks(previousLocks, ParseLockedFields(config.LockedFieldsJson));
        if (addedLocks.Count > 0)
            await ApplyClientLockCascadeAsync(clientId, addedLocks, updatedBy);

        _resolver.ClearCache();
        await _audit.LogChangeAsync("Client", config.Id, "*", null, null, "Full update", updatedBy);
        return config;
    }

    public async Task<ClientConfiguration> PatchClientAsync(Guid clientId, Dictionary<string, object> updates, string? updatedBy = null)
    {
        var blockedFields = await GetGlobalBlockedFieldsAsync();

        var config = await _clientRepo.GetByClientIdAsync(clientId);
        var isNew = config is null;
        var previousLocks = ParseLockedFields(config?.LockedFieldsJson);
        if (config is null)
        {
            config = new ClientConfiguration { ClientId = clientId };
        }

        foreach (var (key, value) in updates)
        {
            var prop = typeof(ClientConfiguration).GetProperty(key,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop is null || !prop.CanWrite) continue;
            var oldValue = prop.GetValue(config)?.ToString();
            var converted = ConvertToPropertyValue(value, prop.PropertyType);
            converted = ProtectPatchValue(key, converted);
            EnsureAllowedPatchValue("Client", key, converted, blockedFields);
            prop.SetValue(config, converted);
            await _audit.LogChangeAsync("Client", config.Id, key, MaskForAudit(key, oldValue), MaskForAudit(key, converted?.ToString()), null, updatedBy);
        }

        NormalizeNullableBooleanDefaults(config);
        ProtectSensitiveData(config);

        config.Version++;
        config.UpdatedBy = updatedBy;
        if (isNew)
            await _clientRepo.CreateAsync(config);
        else
            await _clientRepo.UpdateAsync(config);

        var addedLocks = GetAddedLocks(previousLocks, ParseLockedFields(config.LockedFieldsJson));
        if (addedLocks.Count > 0)
            await ApplyClientLockCascadeAsync(clientId, addedLocks, updatedBy);

        _resolver.ClearCache();
        return config;
    }

    public async Task DeleteClientConfigAsync(Guid clientId, string? deletedBy = null)
    {
        var existing = await _clientRepo.GetByClientIdAsync(clientId);
        if (existing is null) return;
        await _clientRepo.DeleteAsync(clientId);
        _resolver.ClearCache();
        await _audit.LogChangeAsync("Client", existing.Id, "*", null, null, "Deleted (reset to server inheritance)", deletedBy);
    }

    public async Task ResetClientPropertyAsync(Guid clientId, string propertyName, string? resetBy = null)
    {
        var config = await _clientRepo.GetByClientIdAsync(clientId);
        if (config is null) return;

        var prop = typeof(ClientConfiguration).GetProperty(propertyName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
        if (prop is null || !prop.CanWrite) return;
        if (!IsNullableProperty(prop.PropertyType))
            throw new InvalidOperationException($"Property '{propertyName}' is not inheritable (not nullable).");

        var oldValue = prop.GetValue(config)?.ToString();
        if (Nullable.GetUnderlyingType(prop.PropertyType) == typeof(bool))
            prop.SetValue(config, false);
        else
            prop.SetValue(config, null);
        await _audit.LogChangeAsync("Client", config.Id, propertyName, oldValue, null, "Reset to inherit", resetBy);
        config.Version++;
        config.UpdatedBy = resetBy;
        await _clientRepo.UpdateAsync(config);
        _resolver.ClearCache();
    }

    // ============ Site ============

    public async Task<SiteConfiguration?> GetSiteConfigAsync(Guid siteId)
        => await _siteRepo.GetBySiteIdAsync(siteId);

    public async Task<SiteConfiguration> CreateSiteConfigAsync(Guid siteId, SiteConfiguration config, string? createdBy = null)
    {
        var site = await _siteRepository.GetByIdAsync(siteId)
            ?? throw new InvalidOperationException($"Site '{siteId}' not found.");

        var blockedFields = await GetBlockedFieldsForClientAsync(site.ClientId);
        EnsureNoBlockedOverrides(config, blockedFields, "Site");

        NormalizeNullableBooleanDefaults(config);

        config.SiteId = siteId;
        config.ClientId = site.ClientId;
        config.CreatedBy = createdBy;
        config.UpdatedBy = createdBy;
        await _siteRepo.CreateAsync(config);
        _resolver.ClearCache();
        await _audit.LogChangeAsync("Site", config.Id, "*", null, null, "Created", createdBy);
        return config;
    }

    public async Task<SiteConfiguration> UpdateSiteAsync(Guid siteId, SiteConfiguration config, string? updatedBy = null)
    {
        var existing = await _siteRepo.GetBySiteIdAsync(siteId);
        if (existing is null)
            return await CreateSiteConfigAsync(siteId, config, updatedBy);

        var blockedFields = await GetBlockedFieldsForClientAsync(existing.ClientId);
        EnsureNoBlockedOverrides(config, blockedFields, "Site");

        NormalizeNullableBooleanDefaults(config);

        config.Id = existing.Id;
        config.SiteId = siteId;
        config.ClientId = existing.ClientId;
        config.CreatedAt = existing.CreatedAt;
        config.CreatedBy = existing.CreatedBy;
        config.Version = existing.Version + 1;
        config.UpdatedBy = updatedBy;
        ProtectSensitiveData(config);
        await _siteRepo.UpdateAsync(config);
        _resolver.ClearCache();
        await _audit.LogChangeAsync("Site", config.Id, "*", null, null, "Full update", updatedBy);
        return config;
    }

    public async Task<SiteConfiguration> PatchSiteAsync(Guid siteId, Dictionary<string, object> updates, string? updatedBy = null)
    {
        var config = await _siteRepo.GetBySiteIdAsync(siteId);
        var isNew = config is null;
        Guid clientId;
        if (config is null)
        {
            var site = await _siteRepository.GetByIdAsync(siteId)
                ?? throw new InvalidOperationException($"Site '{siteId}' not found.");

            clientId = site.ClientId;

            config = new SiteConfiguration
            {
                SiteId = siteId,
                ClientId = site.ClientId,
                CreatedBy = updatedBy,
                UpdatedBy = updatedBy
            };
        }
        else
        {
            clientId = config.ClientId;
        }

        var blockedFields = await GetBlockedFieldsForClientAsync(clientId);

        foreach (var (key, value) in updates)
        {
            var prop = typeof(SiteConfiguration).GetProperty(key,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop is null || !prop.CanWrite) continue;
            var oldValue = prop.GetValue(config)?.ToString();
            var converted = ConvertToPropertyValue(value, prop.PropertyType);
            converted = ProtectPatchValue(key, converted);
            EnsureAllowedPatchValue("Site", key, converted, blockedFields);
            prop.SetValue(config, converted);
            await _audit.LogChangeAsync("Site", config.Id, key, MaskForAudit(key, oldValue), MaskForAudit(key, converted?.ToString()), null, updatedBy);
        }

        NormalizeNullableBooleanDefaults(config);
        ProtectSensitiveData(config);

        config.Version++;
        config.UpdatedBy = updatedBy;
        if (isNew)
            await _siteRepo.CreateAsync(config);
        else
            await _siteRepo.UpdateAsync(config);
        _resolver.ClearCache();
        return config;
    }

    public async Task DeleteSiteConfigAsync(Guid siteId, string? deletedBy = null)
    {
        var existing = await _siteRepo.GetBySiteIdAsync(siteId);
        if (existing is null) return;
        await _siteRepo.DeleteAsync(siteId);
        _resolver.ClearCache();
        await _audit.LogChangeAsync("Site", existing.Id, "*", null, null, "Deleted (reset to client/server inheritance)", deletedBy);
    }

    public async Task ResetSitePropertyAsync(Guid siteId, string propertyName, string? resetBy = null)
    {
        var config = await _siteRepo.GetBySiteIdAsync(siteId);
        if (config is null) return;

        var prop = typeof(SiteConfiguration).GetProperty(propertyName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
        if (prop is null || !prop.CanWrite) return;
        if (!IsNullableProperty(prop.PropertyType))
            throw new InvalidOperationException($"Property '{propertyName}' is not inheritable (not nullable).");

        var oldValue = prop.GetValue(config)?.ToString();
        if (Nullable.GetUnderlyingType(prop.PropertyType) == typeof(bool))
            prop.SetValue(config, false);
        else
            prop.SetValue(config, null);
        await _audit.LogChangeAsync("Site", config.Id, propertyName, oldValue, null, "Reset to inherit", resetBy);
        config.Version++;
        config.UpdatedBy = resetBy;
        await _siteRepo.UpdateAsync(config);
        _resolver.ClearCache();
    }

    // ============ Validação ============

    public async Task<(bool IsValid, string[] Errors)> ValidateAsync(object config)
    {
        var errors = new List<string>();
        var type = config.GetType();

        CheckRange(type, config, "InventoryIntervalHours", 1, 168, errors);
        CheckRange(type, config, "AgentHeartbeatIntervalSeconds", 10, 3600, errors);
        CheckRange(type, config, "AgentOnlineGraceSeconds", 60, 3600, errors);
        CheckRange(type, config, "NatsAgentJwtTtlMinutes", 1, 1440, errors);
        CheckRange(type, config, "NatsUserJwtTtlMinutes", 1, 1440, errors);

        if (config is ServerConfiguration serverConfiguration)
        {
            var settings = TicketAttachmentSettings.FromJson(serverConfiguration.TicketAttachmentSettingsJson);
            errors.AddRange(settings.Validate());
            errors.AddRange(ValidateAgentUpdatePolicyJson(serverConfiguration.AgentUpdatePolicyJson, nameof(ServerConfiguration.AgentUpdatePolicyJson)));

            if (serverConfiguration.NatsAuthEnabled && !serverConfiguration.NatsUseScopedSubjects)
                errors.Add("NatsUseScopedSubjects must remain enabled while NATS auth is active.");

            // Validate NATS server hosts (apenas formato — não testa conectividade pois o servidor pode exigir auth)
            if (string.IsNullOrWhiteSpace(serverConfiguration.NatsServerHostInternal))
                errors.Add("NatsServerHostInternal cannot be empty.");

            if (string.IsNullOrWhiteSpace(serverConfiguration.NatsServerHostExternal))
                errors.Add("NatsServerHostExternal cannot be empty.");
        }
        else if (config is ClientConfiguration clientConfiguration)
        {
            errors.AddRange(ValidateAgentUpdatePolicyJson(clientConfiguration.AgentUpdatePolicyJson, nameof(ClientConfiguration.AgentUpdatePolicyJson)));
        }
        else if (config is SiteConfiguration siteConfiguration)
        {
            errors.AddRange(ValidateAgentUpdatePolicyJson(siteConfiguration.AgentUpdatePolicyJson, nameof(SiteConfiguration.AgentUpdatePolicyJson)));
        }

        return (errors.Count == 0, errors.ToArray());
    }

    public Task<(bool IsValid, string[] Errors)> ValidateJsonAsync(string objectType, string json)
    {
        try
        {
            JsonDocument.Parse(json);
            return Task.FromResult((true, Array.Empty<string>()));
        }
        catch (JsonException ex)
        {
            return Task.FromResult((false, new[] { $"Invalid JSON for {objectType}: {ex.Message}" }));
        }
    }

    private static void CheckRange(Type type, object obj, string propName, int min, int max, List<string> errors)
    {
        var prop = type.GetProperty(propName);
        if (prop?.GetValue(obj) is int value && (value < min || value > max))
            errors.Add($"{propName} must be between {min} and {max}.");
    }

    private static object? ConvertToPropertyValue(object value, Type propertyType)
    {
        var isNullable = IsNullableProperty(propertyType);
        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (value is JsonElement je)
            return ConvertJsonElement(je, targetType, isNullable);

        if (value is null)
            return isNullable ? null : Activator.CreateInstance(targetType);

        if (targetType.IsEnum)
            return value is string s
                ? Enum.Parse(targetType, s, ignoreCase: true)
                : Enum.ToObject(targetType, value);

        return Convert.ChangeType(value, targetType);
    }

    private static object? ConvertJsonElement(JsonElement element, Type targetType, bool isNullable)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return isNullable ? null : Activator.CreateInstance(targetType);

        if (targetType == typeof(bool))
            return element.GetBoolean();

        if (targetType == typeof(int))
            return element.GetInt32();

        if (targetType == typeof(string))
            return element.GetString();

        if (targetType.IsEnum)
            return Enum.Parse(targetType, element.GetRawText().Trim('"'), ignoreCase: true);

        return element.GetRawText();
    }

    private static bool IsNullableProperty(Type propertyType)
        => !propertyType.IsValueType || Nullable.GetUnderlyingType(propertyType) is not null;

    private static void NormalizeNullableBooleanDefaults(object target)
    {
        var props = target.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        foreach (var prop in props)
        {
            if (Nullable.GetUnderlyingType(prop.PropertyType) != typeof(bool) || !prop.CanWrite)
                continue;

            if (prop.GetValue(target) is null)
                prop.SetValue(target, false);
        }
    }

    private async Task<HashSet<string>> GetGlobalBlockedFieldsAsync()
    {
        var server = await _serverRepo.GetOrCreateDefaultAsync();
        return ParseLockedFields(server.LockedFieldsJson);
    }

    private async Task<HashSet<string>> GetBlockedFieldsForClientAsync(Guid clientId)
    {
        var server = await _serverRepo.GetOrCreateDefaultAsync();
        var client = await _clientRepo.GetByClientIdAsync(clientId);

        var blocked = ParseLockedFields(server.LockedFieldsJson);
        blocked.UnionWith(ParseLockedFields(client?.LockedFieldsJson));
        return blocked;
    }

    private static HashSet<string> ParseLockedFields(string? lockedFieldsJson)
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

    private static void EnsureAllowedPatchValue(string level, string fieldName, object? value, HashSet<string> blockedFields)
    {
        // null no patch significa "voltar para herança" e é permitido mesmo quando bloqueado.
        if (value is null) return;

        var normalizedFieldName = ConfigurationFieldCatalog.NormalizeFieldName(fieldName);
        if (blockedFields.Contains(normalizedFieldName))
            throw new InvalidOperationException($"Field '{fieldName}' is blocked at a higher level and cannot be overridden at {level} level.");
    }

    private static void EnsureNoBlockedOverrides(object config, HashSet<string> blockedFields, string level)
    {
        var props = config.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        foreach (var prop in props)
        {
            if (prop.Name.Equals(nameof(ClientConfiguration.LockedFieldsJson), StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals(nameof(SiteConfiguration.LockedFieldsJson), StringComparison.OrdinalIgnoreCase))
                continue;

            var normalizedPropertyName = ConfigurationFieldCatalog.NormalizeFieldName(prop.Name);
            if (!blockedFields.Contains(normalizedPropertyName))
                continue;

            var value = prop.GetValue(config);
            if (value is not null)
                throw new InvalidOperationException($"Field '{prop.Name}' is blocked at a higher level and cannot be overridden at {level} level.");
        }
    }

    private static HashSet<string> GetAddedLocks(HashSet<string> previousLocks, HashSet<string> currentLocks)
    {
        var added = new HashSet<string>(currentLocks, StringComparer.OrdinalIgnoreCase);
        added.ExceptWith(previousLocks);
        return added;
    }

    private async Task ApplyServerLockCascadeAsync(HashSet<string> addedLocks, string? changedBy)
    {
        var clients = await _clientRepo.GetAllAsync();
        foreach (var client in clients)
        {
            var changedClient = RemoveLocksAndOverrides(client, addedLocks, removeLocalLocks: true);
            if (changedClient)
            {
                client.Version++;
                client.UpdatedBy = changedBy;
                await _clientRepo.UpdateAsync(client);
            }

            var siteConfigs = await _siteRepo.GetByClientIdAsync(client.ClientId);
            foreach (var site in siteConfigs)
            {
                var changedSite = RemoveLocksAndOverrides(site, addedLocks, removeLocalLocks: true);
                if (!changedSite) continue;

                site.Version++;
                site.UpdatedBy = changedBy;
                await _siteRepo.UpdateAsync(site);
            }
        }
    }

    private async Task ApplyClientLockCascadeAsync(Guid clientId, HashSet<string> addedLocks, string? changedBy)
    {
        var siteConfigs = await _siteRepo.GetByClientIdAsync(clientId);
        foreach (var site in siteConfigs)
        {
            var changed = RemoveLocksAndOverrides(site, addedLocks, removeLocalLocks: true);
            if (!changed) continue;

            site.Version++;
            site.UpdatedBy = changedBy;
            await _siteRepo.UpdateAsync(site);
        }
    }

    private static bool RemoveLocksAndOverrides(ClientConfiguration config, HashSet<string> fields, bool removeLocalLocks)
    {
        var changed = false;

        foreach (var field in fields)
            changed |= ClearProperty(config, field);

        if (removeLocalLocks)
        {
            var locks = ParseLockedFields(config.LockedFieldsJson);
            if (locks.RemoveWhere(f => fields.Contains(f)) > 0)
            {
                config.LockedFieldsJson = JsonSerializer.Serialize(locks.OrderBy(x => x).ToArray(), JsonSerializerOptions.Web);
                changed = true;
            }
        }

        return changed;
    }

    private static bool RemoveLocksAndOverrides(SiteConfiguration config, HashSet<string> fields, bool removeLocalLocks)
    {
        var changed = false;

        foreach (var field in fields)
            changed |= ClearProperty(config, field);

        if (removeLocalLocks)
        {
            var locks = ParseLockedFields(config.LockedFieldsJson);
            if (locks.RemoveWhere(f => fields.Contains(f)) > 0)
            {
                config.LockedFieldsJson = JsonSerializer.Serialize(locks.OrderBy(x => x).ToArray(), JsonSerializerOptions.Web);
                changed = true;
            }
        }

        return changed;
    }

    private static bool ClearProperty(object target, string propertyName)
    {
        var prop = target.GetType().GetProperty(propertyName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);

        if (prop is null || !prop.CanWrite)
            return false;

        if (!IsNullableProperty(prop.PropertyType))
            return false;

        if (prop.GetValue(target) is null)
            return false;

        prop.SetValue(target, null);
        return true;
    }

    private static IEnumerable<string> ValidateAgentUpdatePolicyJson(string? json, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var policy = JsonSerializer.Deserialize<AgentUpdatePolicy>(json, JsonSerializerOptions.Web) ?? new AgentUpdatePolicy();
            return policy.Validate().Select(error => $"{fieldName}: {error}");
        }
        catch (JsonException ex)
        {
            return [$"{fieldName}: invalid JSON ({ex.Message})"];
        }
    }

    private void ProtectSensitiveData(ServerConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.ObjectStorageSecretKey))
            config.ObjectStorageSecretKey = _secretProtector.Protect(config.ObjectStorageSecretKey);

        if (!string.IsNullOrWhiteSpace(config.NatsAccountSeed))
            config.NatsAccountSeed = _secretProtector.Protect(config.NatsAccountSeed);

        if (!string.IsNullOrWhiteSpace(config.NatsXKeySeed))
            config.NatsXKeySeed = _secretProtector.Protect(config.NatsXKeySeed);

        config.AIIntegrationSettingsJson = ProtectAiJson(config.AIIntegrationSettingsJson);
    }

    private void ProtectSensitiveData(ClientConfiguration config)
    {
        // Client/Site armazenam apenas campos sobrescritíveis (AIIntegrationSettingsOverride).
        // ApiKey e campos globais são removidos na sanitização — sempre herdados do servidor.
        config.AIIntegrationSettingsJson = SanitizeAiOverrideJson(config.AIIntegrationSettingsJson);
    }

    private void ProtectSensitiveData(SiteConfiguration config)
    {
        config.AIIntegrationSettingsJson = SanitizeAiOverrideJson(config.AIIntegrationSettingsJson);
    }

    private object? ProtectPatchValue(string key, object? converted)
    {
        if (converted is null)
            return null;

        if (key.Equals(nameof(ServerConfiguration.ObjectStorageSecretKey), StringComparison.OrdinalIgnoreCase) &&
            converted is string secret &&
            !string.IsNullOrWhiteSpace(secret))
        {
            return _secretProtector.Protect(secret);
        }

        if (key.Equals(nameof(ServerConfiguration.NatsAccountSeed), StringComparison.OrdinalIgnoreCase) &&
            converted is string seed &&
            !string.IsNullOrWhiteSpace(seed))
        {
            return _secretProtector.Protect(seed);
        }

        if (key.Equals(nameof(ServerConfiguration.NatsXKeySeed), StringComparison.OrdinalIgnoreCase) &&
            converted is string xkeySeed &&
            !string.IsNullOrWhiteSpace(xkeySeed))
        {
            return _secretProtector.Protect(xkeySeed);
        }

        if (key.Equals(nameof(ServerConfiguration.AIIntegrationSettingsJson), StringComparison.OrdinalIgnoreCase) ||
            key.Equals(nameof(ClientConfiguration.AIIntegrationSettingsJson), StringComparison.OrdinalIgnoreCase) ||
            key.Equals(nameof(SiteConfiguration.AIIntegrationSettingsJson), StringComparison.OrdinalIgnoreCase))
        {
            return converted is string json ? ProtectAiJson(json) : converted;
        }

        return converted;
    }

    private string ProtectAiJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        try
        {
            var ai = JsonSerializer.Deserialize<AIIntegrationSettings>(json, JsonSerializerOptions.Web);
            if (ai is null)
                return json;

            if (!string.IsNullOrWhiteSpace(ai.ApiKey))
                ai.ApiKey = _secretProtector.Protect(ai.ApiKey);

            return JsonSerializer.Serialize(ai, JsonSerializerOptions.Web);
        }
        catch
        {
            return json;
        }
    }

    private static string? SanitizeAiOverrideJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var full = JsonSerializer.Deserialize<AIIntegrationSettings>(json, JsonSerializerOptions.Web);
            if (full is null)
                return null;

            var ov = new AIIntegrationSettingsOverride
            {
                Enabled              = full.Enabled,
                ChatAIEnabled        = full.ChatAIEnabled,
                KnowledgeBaseEnabled = full.KnowledgeBaseEnabled,
                ChatModel            = full.ChatModel,
                PromptTemplate       = full.PromptTemplate,
                Temperature          = full.Temperature,
                MaxTokensPerRequest  = full.MaxTokensPerRequest,
                MaxHistoryMessages   = full.MaxHistoryMessages,
                MaxKbContextTokens   = full.MaxKbContextTokens,
                MaxKbChunks          = full.MaxKbChunks,
                MinSimilarityScore   = full.MinSimilarityScore,
            };

            return JsonSerializer.Serialize(ov, JsonSerializerOptions.Web);
        }
        catch
        {
            return json;
        }
    }

    private static string? MaskForAudit(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (!IsSensitiveField(key))
            return value;

        return "***redacted***";
    }

    private static bool IsSensitiveField(string key)
    {
        return key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
             key.Equals(nameof(ServerConfiguration.NatsAccountSeed), StringComparison.OrdinalIgnoreCase) ||
               key.Equals(nameof(ServerConfiguration.NatsXKeySeed), StringComparison.OrdinalIgnoreCase) ||
               key.Equals(nameof(ServerConfiguration.AIIntegrationSettingsJson), StringComparison.OrdinalIgnoreCase) ||
               key.Equals(nameof(ClientConfiguration.AIIntegrationSettingsJson), StringComparison.OrdinalIgnoreCase) ||
               key.Equals(nameof(SiteConfiguration.AIIntegrationSettingsJson), StringComparison.OrdinalIgnoreCase);
    }
}

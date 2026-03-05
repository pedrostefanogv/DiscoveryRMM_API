using System.Text.Json;
using Meduza.Core.Entities;
using Meduza.Core.Interfaces;

namespace Meduza.Infrastructure.Services;

public class ConfigurationService : IConfigurationService
{
    private readonly IServerConfigurationRepository _serverRepo;
    private readonly IClientConfigurationRepository _clientRepo;
    private readonly ISiteConfigurationRepository _siteRepo;
    private readonly ISiteRepository _siteRepository;
    private readonly IConfigurationAuditService _audit;
    private readonly IConfigurationResolver _resolver;

    public ConfigurationService(
        IServerConfigurationRepository serverRepo,
        IClientConfigurationRepository clientRepo,
        ISiteConfigurationRepository siteRepo,
        ISiteRepository siteRepository,
        IConfigurationAuditService audit,
        IConfigurationResolver resolver)
    {
        _serverRepo = serverRepo;
        _clientRepo = clientRepo;
        _siteRepo = siteRepo;
        _siteRepository = siteRepository;
        _audit = audit;
        _resolver = resolver;
    }

    // ============ Server ============

    public async Task<ServerConfiguration> GetServerConfigAsync()
        => await _serverRepo.GetOrCreateDefaultAsync();

    public async Task<ServerConfiguration> UpdateServerAsync(ServerConfiguration config, string? updatedBy = null)
    {
        var existing = await _serverRepo.GetOrCreateDefaultAsync();
        config.Id = existing.Id;
        config.CreatedAt = existing.CreatedAt;
        config.CreatedBy = existing.CreatedBy;
        config.Version = existing.Version + 1;
        config.UpdatedBy = updatedBy;
        await _serverRepo.UpdateAsync(config);
        _resolver.ClearCache();
        await _audit.LogChangeAsync("Server", config.Id, "*", null, null, "Full update", updatedBy);
        return config;
    }

    public async Task<ServerConfiguration> PatchServerAsync(Dictionary<string, object> updates, string? updatedBy = null)
    {
        var config = await _serverRepo.GetOrCreateDefaultAsync();
        foreach (var (key, value) in updates)
        {
            var prop = typeof(ServerConfiguration).GetProperty(key,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop is null || !prop.CanWrite) continue;
            var oldValue = prop.GetValue(config)?.ToString();
            var converted = ConvertToPropertyValue(value, prop.PropertyType);
            prop.SetValue(config, converted);
            await _audit.LogChangeAsync("Server", config.Id, key, oldValue, converted?.ToString(), null, updatedBy);
        }
        config.Version++;
        config.UpdatedBy = updatedBy;
        await _serverRepo.UpdateAsync(config);
        _resolver.ClearCache();
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
        await _audit.LogChangeAsync("Server", existing.Id, "*", null, null, "Reset to defaults", resetBy);
        return reset;
    }

    // ============ Client ============

    public async Task<ClientConfiguration?> GetClientConfigAsync(Guid clientId)
        => await _clientRepo.GetByClientIdAsync(clientId);

    public async Task<ClientConfiguration> CreateClientConfigAsync(Guid clientId, ClientConfiguration config, string? createdBy = null)
    {
        config.ClientId = clientId;
        config.CreatedBy = createdBy;
        config.UpdatedBy = createdBy;
        await _clientRepo.CreateAsync(config);
        await _audit.LogChangeAsync("Client", config.Id, "*", null, null, "Created", createdBy);
        return config;
    }

    public async Task<ClientConfiguration> UpdateClientAsync(Guid clientId, ClientConfiguration config, string? updatedBy = null)
    {
        var existing = await _clientRepo.GetByClientIdAsync(clientId);
        if (existing is null)
            return await CreateClientConfigAsync(clientId, config, updatedBy);

        config.Id = existing.Id;
        config.ClientId = clientId;
        config.CreatedAt = existing.CreatedAt;
        config.CreatedBy = existing.CreatedBy;
        config.Version = existing.Version + 1;
        config.UpdatedBy = updatedBy;
        await _clientRepo.UpdateAsync(config);
        _resolver.ClearCache();
        await _audit.LogChangeAsync("Client", config.Id, "*", null, null, "Full update", updatedBy);
        return config;
    }

    public async Task<ClientConfiguration> PatchClientAsync(Guid clientId, Dictionary<string, object> updates, string? updatedBy = null)
    {
        var config = await _clientRepo.GetByClientIdAsync(clientId);
        if (config is null)
        {
            config = new ClientConfiguration { ClientId = clientId };
            await _clientRepo.CreateAsync(config);
        }

        foreach (var (key, value) in updates)
        {
            var prop = typeof(ClientConfiguration).GetProperty(key,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop is null || !prop.CanWrite) continue;
            var oldValue = prop.GetValue(config)?.ToString();
            var converted = ConvertToPropertyValue(value, prop.PropertyType);
            prop.SetValue(config, converted);
            await _audit.LogChangeAsync("Client", config.Id, key, oldValue, converted?.ToString(), null, updatedBy);
        }
        config.Version++;
        config.UpdatedBy = updatedBy;
        await _clientRepo.UpdateAsync(config);
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

        config.Id = existing.Id;
        config.SiteId = siteId;
        config.ClientId = existing.ClientId;
        config.CreatedAt = existing.CreatedAt;
        config.CreatedBy = existing.CreatedBy;
        config.Version = existing.Version + 1;
        config.UpdatedBy = updatedBy;
        await _siteRepo.UpdateAsync(config);
        _resolver.ClearCache();
        await _audit.LogChangeAsync("Site", config.Id, "*", null, null, "Full update", updatedBy);
        return config;
    }

    public async Task<SiteConfiguration> PatchSiteAsync(Guid siteId, Dictionary<string, object> updates, string? updatedBy = null)
    {
        var config = await _siteRepo.GetBySiteIdAsync(siteId);
        if (config is null)
        {
            config = await CreateSiteConfigAsync(siteId, new SiteConfiguration(), updatedBy);
        }

        foreach (var (key, value) in updates)
        {
            var prop = typeof(SiteConfiguration).GetProperty(key,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop is null || !prop.CanWrite) continue;
            var oldValue = prop.GetValue(config)?.ToString();
            var converted = ConvertToPropertyValue(value, prop.PropertyType);
            prop.SetValue(config, converted);
            await _audit.LogChangeAsync("Site", config.Id, key, oldValue, converted?.ToString(), null, updatedBy);
        }
        config.Version++;
        config.UpdatedBy = updatedBy;
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
        prop.SetValue(config, null);
        await _audit.LogChangeAsync("Site", config.Id, propertyName, oldValue, null, "Reset to inherit", resetBy);
        config.Version++;
        config.UpdatedBy = resetBy;
        await _siteRepo.UpdateAsync(config);
        _resolver.ClearCache();
    }

    // ============ Validação ============

    public Task<(bool IsValid, string[] Errors)> ValidateAsync(object config)
    {
        var errors = new List<string>();
        var type = config.GetType();

        CheckRange(type, config, "InventoryIntervalHours", 1, 168, errors);
        CheckRange(type, config, "AgentHeartbeatIntervalSeconds", 10, 3600, errors);
        CheckRange(type, config, "AgentOfflineThresholdSeconds", 30, 86400, errors);
        CheckRange(type, config, "TokenExpirationDays", 1, 3650, errors);
        CheckRange(type, config, "MaxTokensPerAgent", 1, 100, errors);

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
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
}

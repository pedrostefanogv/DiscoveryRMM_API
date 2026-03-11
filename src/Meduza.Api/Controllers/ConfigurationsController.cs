using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Configuration;
using System.Text.Json;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Meduza.Api.Controllers;

/// <summary>
/// Gerencia configurações hierárquicas: Servidor → Cliente → Site.
/// Valores null em cliente/site indicam herança do nível superior.
/// </summary>
[ApiController]
[Route("api/configurations")]
public class ConfigurationsController : ControllerBase
{
    private readonly IConfigurationService _configService;
    private readonly IConfigurationResolver _resolver;
    private readonly IClientRepository _clientRepository;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;

    public ConfigurationsController(
        IConfigurationService configService,
        IConfigurationResolver resolver,
        IClientRepository clientRepository,
        IOptionsMonitor<ReportingOptions> reportingOptions)
    {
        _configService = configService;
        _resolver = resolver;
        _clientRepository = clientRepository;
        _reportingOptions = reportingOptions;
    }

    // ============ Server ============

    [HttpGet("server")]
    public async Task<IActionResult> GetServer()
    {
        var config = await _configService.GetServerConfigAsync();
        return Ok(SanitizeServerConfiguration(config));
    }

    [HttpPut("server")]
    public async Task<IActionResult> UpdateServer([FromBody] ServerConfiguration config)
    {
        var (isValid, errors) = await _configService.ValidateAsync(config);
        if (!isValid) return BadRequest(new { errors });

        var updated = await _configService.UpdateServerAsync(config,
            HttpContext.Items["Username"] as string ?? "api");
        return Ok(SanitizeServerConfiguration(updated));
    }

    [HttpPatch("server")]
    public async Task<IActionResult> PatchServer([FromBody] Dictionary<string, object> updates)
    {
        var updated = await _configService.PatchServerAsync(updates,
            HttpContext.Items["Username"] as string ?? "api");
        return Ok(SanitizeServerConfiguration(updated));
    }

    [HttpPost("server/reset")]
    public async Task<IActionResult> ResetServer()
    {
        var reset = await _configService.ResetServerAsync(
            HttpContext.Items["Username"] as string ?? "api");
        return Ok(SanitizeServerConfiguration(reset));
    }

    [HttpGet("server/metadata")]
    public async Task<IActionResult> GetServerMetadata()
    {
        var server = await _configService.GetServerConfigAsync();
        var globalLocks = ParseLockedFields(server.LockedFieldsJson);

        var fields = ManagedFields.ToDictionary(
            field => field,
            field => new ConfigurationFieldMetadata
            {
                Field = field,
                SourceType = 2,
                IsLockedByGlobal = globalLocks.Contains(field),
                IsLockedByClient = false,
                IsLockedBySite = false,
                CanEditAtClient = !globalLocks.Contains(field),
                CanEditAtSite = !globalLocks.Contains(field),
                CanEditAtAgent = !globalLocks.Contains(field),
                LockOwnerForClient = globalLocks.Contains(field) ? 2 : null,
                LockOwnerForSite = globalLocks.Contains(field) ? 2 : null,
                LockOwnerForAgent = globalLocks.Contains(field) ? 2 : null,
            },
            StringComparer.OrdinalIgnoreCase);

        return Ok(new
        {
            level = "Server",
            globalLockedFields = globalLocks.OrderBy(x => x).ToArray(),
            fields
        });
    }

    [HttpGet("server/reporting")]
    public async Task<IActionResult> GetServerReporting()
    {
        var server = await _configService.GetServerConfigAsync();
        var optionsFromFile = _reportingOptions.CurrentValue;
        var current = DeserializeReporting(server.ReportingSettingsJson) ?? BuildDefaultReporting(optionsFromFile);

        return Ok(current);
    }

    [HttpPut("server/reporting")]
    public async Task<IActionResult> UpdateServerReporting([FromBody] ReportingSettingsRequest request)
    {
        var optionsFromFile = _reportingOptions.CurrentValue;
        var allowed = NormalizeAllowedDays(request.AllowedRetentionDays ?? optionsFromFile.AllowedRetentionDays);

        if (!allowed.Contains(request.DatabaseRetentionDays) || !allowed.Contains(request.FileRetentionDays))
        {
            return BadRequest(new
            {
                error = "DatabaseRetentionDays e FileRetentionDays devem estar em AllowedRetentionDays.",
                allowedRetentionDays = allowed
            });
        }

        var server = await _configService.GetServerConfigAsync();

        var normalized = new ReportingSettingsResponse(
            request.EnablePdf,
            request.ProcessingTimeoutSeconds,
            request.FileDownloadTimeoutSeconds,
            request.DatabaseRetentionDays,
            request.FileRetentionDays,
            allowed);

        server.ReportingSettingsJson = JsonSerializer.Serialize(normalized, JsonSerializerOptions.Web);

        await _configService.UpdateServerAsync(server, HttpContext.Items["Username"] as string ?? "api");
        return Ok(normalized);
    }

    // ============ Client ============

    [HttpGet("clients/{clientId:guid}")]
    public async Task<IActionResult> GetClient(Guid clientId)
    {
        var client = await _clientRepository.GetByIdAsync(clientId);
        if (client is null)
            return NotFound(new { error = $"Client '{clientId}' not found." });

        var resolved = await BuildClientEffectiveAsync(clientId);
        return Ok(resolved);
    }

    [HttpGet("clients/{clientId:guid}/effective")]
    public async Task<IActionResult> GetClientEffective(Guid clientId)
    {
        var client = await _clientRepository.GetByIdAsync(clientId);
        if (client is null)
            return NotFound(new { error = $"Client '{clientId}' not found." });

        var resolved = await BuildClientEffectiveAsync(clientId);
        return Ok(resolved);
    }

    [HttpGet("clients/{clientId:guid}/metadata")]
    public async Task<IActionResult> GetClientMetadata(Guid clientId)
    {
        var clientEntity = await _clientRepository.GetByIdAsync(clientId);
        if (clientEntity is null)
            return NotFound(new { error = $"Client '{clientId}' not found." });

        var server = await _configService.GetServerConfigAsync();
        var client = await _configService.GetClientConfigAsync(clientId);

        var globalLocks = ParseLockedFields(server.LockedFieldsJson);
        var clientLocks = ParseLockedFields(client?.LockedFieldsJson);

        var fields = ManagedFields.ToDictionary(
            field => field,
            field =>
            {
                var sourceType = globalLocks.Contains(field)
                    ? 0
                    : IsFieldConfigured(client, field) ? 3 : 2;

                return new ConfigurationFieldMetadata
                {
                    Field = field,
                    SourceType = sourceType,
                    IsLockedByGlobal = globalLocks.Contains(field),
                    IsLockedByClient = clientLocks.Contains(field),
                    IsLockedBySite = false,
                    CanEditAtClient = !globalLocks.Contains(field),
                    CanEditAtSite = !globalLocks.Contains(field) && !clientLocks.Contains(field),
                    CanEditAtAgent = !globalLocks.Contains(field) && !clientLocks.Contains(field),
                    LockOwnerForClient = globalLocks.Contains(field) ? 2 : null,
                    LockOwnerForSite = globalLocks.Contains(field) ? 2 : clientLocks.Contains(field) ? 3 : null,
                    LockOwnerForAgent = globalLocks.Contains(field) ? 2 : clientLocks.Contains(field) ? 3 : null,
                };
            },
            StringComparer.OrdinalIgnoreCase);

        return Ok(new
        {
            level = "Client",
            clientId,
            globalLockedFields = globalLocks.OrderBy(x => x).ToArray(),
            clientLockedFields = clientLocks.OrderBy(x => x).ToArray(),
            fields
        });
    }

    [HttpPut("clients/{clientId:guid}")]
    public async Task<IActionResult> UpsertClient(Guid clientId, [FromBody] ClientConfiguration config)
    {
        var (isValid, errors) = await _configService.ValidateAsync(config);
        if (!isValid) return BadRequest(new { errors });

        var updated = await _configService.UpdateClientAsync(clientId, config,
            HttpContext.Items["Username"] as string ?? "api");
        return Ok(SanitizeClientConfiguration(updated));
    }

    [HttpPatch("clients/{clientId:guid}")]
    public async Task<IActionResult> PatchClient(Guid clientId, [FromBody] Dictionary<string, object> updates)
    {
        var updated = await _configService.PatchClientAsync(clientId, updates,
            HttpContext.Items["Username"] as string ?? "api");
        return Ok(SanitizeClientConfiguration(updated));
    }

    [HttpDelete("clients/{clientId:guid}")]
    public async Task<IActionResult> DeleteClient(Guid clientId)
    {
        await _configService.DeleteClientConfigAsync(clientId,
            HttpContext.Items["Username"] as string ?? "api");
        return NoContent();
    }

    [HttpPost("clients/{clientId:guid}/reset/{propertyName}")]
    public async Task<IActionResult> ResetClientProperty(Guid clientId, string propertyName)
    {
        try
        {
            await _configService.ResetClientPropertyAsync(clientId, propertyName,
                HttpContext.Items["Username"] as string ?? "api");
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ============ Site ============

    [HttpGet("sites/{siteId:guid}")]
    public async Task<IActionResult> GetSite(Guid siteId)
    {
        try
        {
            var resolved = await _resolver.ResolveForSiteAsync(siteId);
            return Ok(SanitizeResolvedConfiguration(resolved));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("sites/{siteId:guid}/effective")]
    public async Task<IActionResult> GetSiteEffective(Guid siteId)
    {
        try
        {
            var resolved = await _resolver.ResolveForSiteAsync(siteId);
            return Ok(SanitizeResolvedConfiguration(resolved));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("sites/{siteId:guid}/metadata")]
    public async Task<IActionResult> GetSiteMetadata(Guid siteId)
    {
        try
        {
            var resolved = await _resolver.ResolveForSiteAsync(siteId);
            var server = await _configService.GetServerConfigAsync();
            var client = resolved.ClientId.HasValue
                ? await _configService.GetClientConfigAsync(resolved.ClientId.Value)
                : null;
            var site = await _configService.GetSiteConfigAsync(siteId);

            var globalLocks = ParseLockedFields(server.LockedFieldsJson);
            var clientLocks = ParseLockedFields(client?.LockedFieldsJson);
            var siteLocks = ParseLockedFields(site?.LockedFieldsJson);

            var fields = ManagedFields.ToDictionary(
                field => field,
                field =>
                {
                    resolved.Inheritance.TryGetValue(field, out var sourceType);
                    if (sourceType == 0)
                    {
                        sourceType = IsFieldConfigured(site, field) ? 4
                            : IsFieldConfigured(client, field) ? 3
                            : 2;
                    }

                    var lockOwnerForSite = globalLocks.Contains(field) ? 2
                        : clientLocks.Contains(field) ? 3
                        : (int?)null;

                    var lockOwnerForAgent = globalLocks.Contains(field) ? 2
                        : clientLocks.Contains(field) ? 3
                        : siteLocks.Contains(field) ? 4
                        : (int?)null;

                    return new ConfigurationFieldMetadata
                    {
                        Field = field,
                        SourceType = sourceType,
                        IsLockedByGlobal = globalLocks.Contains(field),
                        IsLockedByClient = clientLocks.Contains(field),
                        IsLockedBySite = siteLocks.Contains(field),
                        CanEditAtClient = !globalLocks.Contains(field),
                        CanEditAtSite = !globalLocks.Contains(field) && !clientLocks.Contains(field),
                        CanEditAtAgent = !globalLocks.Contains(field) && !clientLocks.Contains(field) && !siteLocks.Contains(field),
                        LockOwnerForClient = globalLocks.Contains(field) ? 2 : null,
                        LockOwnerForSite = lockOwnerForSite,
                        LockOwnerForAgent = lockOwnerForAgent,
                    };
                },
                StringComparer.OrdinalIgnoreCase);

            return Ok(new
            {
                level = "Site",
                siteId,
                clientId = resolved.ClientId,
                globalLockedFields = globalLocks.OrderBy(x => x).ToArray(),
                clientLockedFields = clientLocks.OrderBy(x => x).ToArray(),
                siteLockedFields = siteLocks.OrderBy(x => x).ToArray(),
                fields
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("sites/{siteId:guid}")]
    public async Task<IActionResult> UpsertSite(Guid siteId, [FromBody] SiteConfiguration config)
    {
        var (isValid, errors) = await _configService.ValidateAsync(config);
        if (!isValid) return BadRequest(new { errors });

        try
        {
            var updated = await _configService.UpdateSiteAsync(siteId, config,
                HttpContext.Items["Username"] as string ?? "api");
            return Ok(SanitizeSiteConfiguration(updated));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPatch("sites/{siteId:guid}")]
    public async Task<IActionResult> PatchSite(Guid siteId, [FromBody] Dictionary<string, object> updates)
    {
        try
        {
            var updated = await _configService.PatchSiteAsync(siteId, updates,
                HttpContext.Items["Username"] as string ?? "api");
            return Ok(SanitizeSiteConfiguration(updated));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpDelete("sites/{siteId:guid}")]
    public async Task<IActionResult> DeleteSite(Guid siteId)
    {
        await _configService.DeleteSiteConfigAsync(siteId,
            HttpContext.Items["Username"] as string ?? "api");
        return NoContent();
    }

    [HttpPost("sites/{siteId:guid}/reset/{propertyName}")]
    public async Task<IActionResult> ResetSiteProperty(Guid siteId, string propertyName)
    {
        try
        {
            await _configService.ResetSitePropertyAsync(siteId, propertyName,
                HttpContext.Items["Username"] as string ?? "api");
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static readonly string[] ManagedFields =
    [
        "RecoveryEnabled",
        "DiscoveryEnabled",
        "P2PFilesEnabled",
        "SupportEnabled",
        "KnowledgeBaseEnabled",
        "AppStorePolicy",
        "InventoryIntervalHours",
        "AutoUpdateSettingsJson",
        "AIIntegrationSettingsJson",
        "TokenExpirationDays",
        "MaxTokensPerAgent",
        "AgentHeartbeatIntervalSeconds",
        "AgentOfflineThresholdSeconds"
    ];

    private static HashSet<string> ParseLockedFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json, JsonSerializerOptions.Web) ?? [];
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    private static bool IsFieldConfigured(object? configObject, string field)
    {
        if (configObject is null)
            return false;

        var prop = configObject.GetType().GetProperty(field,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);

        if (prop is null)
            return false;

        var value = prop.GetValue(configObject);
        return value is not null;
    }

    private async Task<object> BuildClientEffectiveAsync(Guid clientId)
    {
        var server = await _configService.GetServerConfigAsync();
        var client = await _configService.GetClientConfigAsync(clientId);

        var globalLocks = ParseLockedFields(server.LockedFieldsJson);
        var clientLocks = ParseLockedFields(client?.LockedFieldsJson);
        var blocked = new HashSet<string>(globalLocks, StringComparer.OrdinalIgnoreCase);
        blocked.UnionWith(clientLocks);

        var autoUpdate = DeserializeOrDefault<AutoUpdateSettings>(client?.AutoUpdateSettingsJson)
            ?? DeserializeOrDefault<AutoUpdateSettings>(server.AutoUpdateSettingsJson)
            ?? new AutoUpdateSettings();

        var ai = DeserializeOrDefault<AIIntegrationSettings>(client?.AIIntegrationSettingsJson)
            ?? DeserializeOrDefault<AIIntegrationSettings>(server.AIIntegrationSettingsJson)
            ?? new AIIntegrationSettings();
        ai.ApiKey = null;

        return new
        {
            ClientId = clientId,
            RecoveryEnabled = client?.RecoveryEnabled ?? server.RecoveryEnabled,
            DiscoveryEnabled = client?.DiscoveryEnabled ?? server.DiscoveryEnabled,
            P2PFilesEnabled = client?.P2PFilesEnabled ?? server.P2PFilesEnabled,
            SupportEnabled = client?.SupportEnabled ?? server.SupportEnabled,
            KnowledgeBaseEnabled = server.KnowledgeBaseEnabled,
            AppStorePolicy = client?.AppStorePolicy ?? server.AppStorePolicy,
            InventoryIntervalHours = client?.InventoryIntervalHours ?? server.InventoryIntervalHours,
            TokenExpirationDays = client?.TokenExpirationDays ?? server.TokenExpirationDays,
            MaxTokensPerAgent = client?.MaxTokensPerAgent ?? server.MaxTokensPerAgent,
            AgentHeartbeatIntervalSeconds = client?.AgentHeartbeatIntervalSeconds ?? server.AgentHeartbeatIntervalSeconds,
            AgentOfflineThresholdSeconds = client?.AgentOfflineThresholdSeconds ?? server.AgentOfflineThresholdSeconds,
            AutoUpdate = autoUpdate,
            AIIntegration = ai,
            HasLocalConfiguration = client is not null,
            BlockedFields = blocked.OrderBy(x => x).ToArray(),
            Inheritance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["RecoveryEnabled"] = client?.RecoveryEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["DiscoveryEnabled"] = client?.DiscoveryEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["P2PFilesEnabled"] = client?.P2PFilesEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["SupportEnabled"] = client?.SupportEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["KnowledgeBaseEnabled"] = (int)ConfigurationPriorityType.Global,
                ["AppStorePolicy"] = client?.AppStorePolicy is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["InventoryIntervalHours"] = client?.InventoryIntervalHours is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["TokenExpirationDays"] = client?.TokenExpirationDays is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["MaxTokensPerAgent"] = client?.MaxTokensPerAgent is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["AgentHeartbeatIntervalSeconds"] = client?.AgentHeartbeatIntervalSeconds is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["AgentOfflineThresholdSeconds"] = client?.AgentOfflineThresholdSeconds is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["AutoUpdate"] = !string.IsNullOrWhiteSpace(client?.AutoUpdateSettingsJson) ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["AIIntegration"] = !string.IsNullOrWhiteSpace(client?.AIIntegrationSettingsJson) ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
            }
        };
    }

    private static T? DeserializeOrDefault<T>(string? json) where T : class, new()
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web) ?? new T();
        }
        catch
        {
            return null;
        }
    }

    private static ServerConfiguration SanitizeServerConfiguration(ServerConfiguration config)
    {
        config.AIIntegrationSettingsJson = SanitizeAiJson(config.AIIntegrationSettingsJson);
        return config;
    }

    private static ClientConfiguration SanitizeClientConfiguration(ClientConfiguration config)
    {
        config.AIIntegrationSettingsJson = SanitizeAiJson(config.AIIntegrationSettingsJson);
        return config;
    }

    private static SiteConfiguration SanitizeSiteConfiguration(SiteConfiguration config)
    {
        config.AIIntegrationSettingsJson = SanitizeAiJson(config.AIIntegrationSettingsJson);
        return config;
    }

    private static ResolvedConfiguration SanitizeResolvedConfiguration(ResolvedConfiguration config)
    {
        if (config.AIIntegration is not null)
            config.AIIntegration.ApiKey = null;
        return config;
    }

    private static string SanitizeAiJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        try
        {
            var ai = JsonSerializer.Deserialize<AIIntegrationSettings>(json, JsonSerializerOptions.Web);
            if (ai is null)
                return json;
            ai.ApiKey = null;
            return JsonSerializer.Serialize(ai, JsonSerializerOptions.Web);
        }
        catch
        {
            return json;
        }
    }

    private static ReportingSettingsResponse BuildDefaultReporting(ReportingOptions options)
    {
        var allowed = NormalizeAllowedDays(options.AllowedRetentionDays);
        var dbDays = allowed.Contains(options.DatabaseRetentionDays) ? options.DatabaseRetentionDays : allowed.Last();
        var fileDays = allowed.Contains(options.FileRetentionDays) ? options.FileRetentionDays : allowed.Last();

        return new ReportingSettingsResponse(
            options.EnablePdf,
            options.ProcessingTimeoutSeconds,
            options.FileDownloadTimeoutSeconds,
            dbDays,
            fileDays,
            allowed);
    }

    private static ReportingSettingsResponse? DeserializeReporting(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<ReportingSettingsResponse>(json, JsonSerializerOptions.Web);
            if (parsed is null)
                return null;

            var allowed = NormalizeAllowedDays(parsed.AllowedRetentionDays);
            var dbDays = allowed.Contains(parsed.DatabaseRetentionDays) ? parsed.DatabaseRetentionDays : allowed.Last();
            var fileDays = allowed.Contains(parsed.FileRetentionDays) ? parsed.FileRetentionDays : allowed.Last();

            return parsed with
            {
                DatabaseRetentionDays = dbDays,
                FileRetentionDays = fileDays,
                AllowedRetentionDays = allowed
            };
        }
        catch
        {
            return null;
        }
    }

    private static int[] NormalizeAllowedDays(int[]? values)
    {
        var allowed = (values is { Length: > 0 } ? values : [30, 60, 90])
            .Where(value => value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        return allowed.Length > 0 ? allowed : [30, 60, 90];
    }
}

public class ConfigurationFieldMetadata
{
    public string Field { get; set; } = string.Empty;
    public int SourceType { get; set; }
    public bool IsLockedByGlobal { get; set; }
    public bool IsLockedByClient { get; set; }
    public bool IsLockedBySite { get; set; }
    public bool CanEditAtClient { get; set; }
    public bool CanEditAtSite { get; set; }
    public bool CanEditAtAgent { get; set; }
    public int? LockOwnerForClient { get; set; }
    public int? LockOwnerForSite { get; set; }
    public int? LockOwnerForAgent { get; set; }
}

public record ReportingSettingsRequest(
    bool EnablePdf,
    int ProcessingTimeoutSeconds,
    int FileDownloadTimeoutSeconds,
    int DatabaseRetentionDays,
    int FileRetentionDays,
    int[]? AllowedRetentionDays);

public record ReportingSettingsResponse(
    bool EnablePdf,
    int ProcessingTimeoutSeconds,
    int FileDownloadTimeoutSeconds,
    int DatabaseRetentionDays,
    int FileRetentionDays,
    int[] AllowedRetentionDays);

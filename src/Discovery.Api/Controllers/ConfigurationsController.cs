using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Configuration;
using System.Text.Json;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Discovery.Api.Services;

namespace Discovery.Api.Controllers;

/// <summary>
/// Gerencia configurações hierárquicas: Servidor → Cliente → Site.
/// Valores null em cliente/site indicam herança do nível superior.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/configurations")]
public class ConfigurationsController : ControllerBase
{
    private readonly IConfigurationService _configService;
    private readonly IConfigurationResolver _resolver;
    private readonly IClientRepository _clientRepository;
    private readonly IOptionsMonitor<ReportingOptions> _reportingOptions;
    private readonly IObjectStorageProviderFactory _storageFactory;
    private readonly ISyncInvalidationPublisher _syncInvalidationPublisher;
    private readonly INatsConnectionValidator _natsConnectionValidator;
    private readonly IConfiguration _configuration;
    private readonly INatsAuthCalloutReloadSignal _natsReloadSignal;
    private readonly IKnowledgeEmbeddingResetService _embeddingResetService;
    private readonly IAiCredentialResolver _aiCredentialResolver;
    private readonly IAiProviderCredentialRepository _aiCredentialRepo;
    private readonly IAiModelCatalogService _aiModelCatalog;
    private readonly ISecretProtector _secretProtector;

    public ConfigurationsController(
        IConfigurationService configService,
        IConfigurationResolver resolver,
        IClientRepository clientRepository,
        IOptionsMonitor<ReportingOptions> reportingOptions,
        IObjectStorageProviderFactory storageFactory,
        ISyncInvalidationPublisher syncInvalidationPublisher,
        INatsConnectionValidator natsConnectionValidator,
        IConfiguration configuration,
        INatsAuthCalloutReloadSignal natsReloadSignal,
        IKnowledgeEmbeddingResetService embeddingResetService,
        IAiCredentialResolver aiCredentialResolver,
        IAiProviderCredentialRepository aiCredentialRepo,
        IAiModelCatalogService aiModelCatalog,
        ISecretProtector secretProtector)
    {
        _configService = configService;
        _resolver = resolver;
        _clientRepository = clientRepository;
        _reportingOptions = reportingOptions;
        _storageFactory = storageFactory;
        _syncInvalidationPublisher = syncInvalidationPublisher;
        _natsConnectionValidator = natsConnectionValidator;
        _configuration = configuration;
        _natsReloadSignal = natsReloadSignal;
        _embeddingResetService = embeddingResetService;
        _aiCredentialResolver = aiCredentialResolver;
        _aiCredentialRepo = aiCredentialRepo;
        _aiModelCatalog = aiModelCatalog;
        _secretProtector = secretProtector;
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

        // Detecta mudança de dimensão de embedding e aciona reset antes de salvar
        var currentServer = await _configService.GetServerConfigAsync();
        var newAiSettings = DeserializeOrDefault<AIIntegrationSettings>(config.AIIntegrationSettingsJson);
        var newDimensions = newAiSettings?.EmbeddingDimensions ?? 1536;
        if (newDimensions != currentServer.CurrentEmbeddingDimensions)
        {
            var actor = HttpContext.Items["Username"] as string ?? "api";
            await _embeddingResetService.ResetAsync(newDimensions, actor);
        }

        var updated = await _configService.UpdateServerAsync(config,
            HttpContext.Items["Username"] as string ?? "api");
        await _syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.Configuration,
            "server-configuration-updated");
        _natsReloadSignal.Signal();
        return Ok(SanitizeServerConfiguration(updated));
    }

    [HttpPatch("server")]
    public async Task<IActionResult> PatchServer([FromBody] Dictionary<string, object> updates)
    {
        var updated = await _configService.PatchServerAsync(updates,
            HttpContext.Items["Username"] as string ?? "api");
        await _syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.Configuration,
            "server-configuration-patched");
        _natsReloadSignal.Signal();
        return Ok(SanitizeServerConfiguration(updated));
    }

    [HttpPost("server/reset")]
    public async Task<IActionResult> ResetServer()
    {
        var reset = await _configService.ResetServerAsync(
            HttpContext.Items["Username"] as string ?? "api");
        await _syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.Configuration,
            "server-configuration-reset");
        return Ok(SanitizeServerConfiguration(reset));
    }

    [HttpPost("server/nats/test")]
    public async Task<IActionResult> TestNatsConnection([FromBody] NatsConnectionTestRequest? request, CancellationToken cancellationToken)
    {
        request ??= new NatsConnectionTestRequest();

        // Fallback para credenciais configuradas em appsettings.json quando não fornecidas no body
        var user = request.User ?? _configuration.GetValue<string>("Nats:AuthUser");
        var password = request.Password ?? _configuration.GetValue<string>("Nats:AuthPassword");

        var (isValid, errors) = await _natsConnectionValidator.ValidateConnectionAsync(
            request.Url ?? string.Empty,
            user,
            password,
            cancellationToken);

        if (!isValid)
            return BadRequest(new { errors });

        return Ok(new { ok = true });
    }

    /// <summary>
    /// Atualiza as configurações NATS do servidor.
    /// TTL de JWT deve estar entre 15 minutos (mínimo) e 4320 minutos / 72h (máximo).
    /// </summary>
    [HttpPatch("server/nats")]
    public async Task<IActionResult> PatchNatsSettings([FromBody] NatsSettingsRequest request)
    {
        const int minTtl = 15;
        const int maxTtl = 4320; // 72h

        var errors = new List<string>();

        if (request.NatsAgentJwtTtlMinutes.HasValue)
        {
            if (request.NatsAgentJwtTtlMinutes.Value < minTtl || request.NatsAgentJwtTtlMinutes.Value > maxTtl)
                errors.Add($"NatsAgentJwtTtlMinutes must be between {minTtl} and {maxTtl} minutes.");
        }

        if (request.NatsUserJwtTtlMinutes.HasValue)
        {
            if (request.NatsUserJwtTtlMinutes.Value < minTtl || request.NatsUserJwtTtlMinutes.Value > maxTtl)
                errors.Add($"NatsUserJwtTtlMinutes must be between {minTtl} and {maxTtl} minutes.");
        }

        if (errors.Count > 0)
            return BadRequest(new { errors });

        var patches = new Dictionary<string, object>();

        if (request.NatsEnabled.HasValue)
            patches[nameof(ServerConfiguration.NatsEnabled)] = request.NatsEnabled.Value;
        if (request.NatsAuthEnabled.HasValue)
            patches[nameof(ServerConfiguration.NatsAuthEnabled)] = request.NatsAuthEnabled.Value;
        if (request.NatsUseWssExternal.HasValue)
            patches[nameof(ServerConfiguration.NatsUseWssExternal)] = request.NatsUseWssExternal.Value;
        if (!string.IsNullOrWhiteSpace(request.NatsServerHostInternal))
            patches[nameof(ServerConfiguration.NatsServerHostInternal)] = request.NatsServerHostInternal;
        if (!string.IsNullOrWhiteSpace(request.NatsServerHostExternal))
            patches[nameof(ServerConfiguration.NatsServerHostExternal)] = request.NatsServerHostExternal;
        if (request.NatsAgentJwtTtlMinutes.HasValue)
            patches[nameof(ServerConfiguration.NatsAgentJwtTtlMinutes)] = request.NatsAgentJwtTtlMinutes.Value;
        if (request.NatsUserJwtTtlMinutes.HasValue)
            patches[nameof(ServerConfiguration.NatsUserJwtTtlMinutes)] = request.NatsUserJwtTtlMinutes.Value;

        if (patches.Count == 0)
            return BadRequest(new { errors = new[] { "No fields provided to update." } });

        var updated = await _configService.PatchServerAsync(patches,
            HttpContext.Items["Username"] as string ?? "api");

        await _syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.Configuration,
            "server-nats-settings-patched");

        _natsReloadSignal.Signal();

        return Ok(SanitizeServerConfiguration(updated));
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
        var normalizedMaxConcurrent = Math.Clamp(request.MaxConcurrentExecutions, 1, 16);
        var normalizedProcessingTimeout = Math.Clamp(request.ProcessingTimeoutSeconds, 30, 3600);
        var normalizedFileDownloadTimeout = Math.Clamp(request.FileDownloadTimeoutSeconds, 5, 600);

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
            normalizedProcessingTimeout,
            normalizedFileDownloadTimeout,
            normalizedMaxConcurrent,
            request.DatabaseRetentionDays,
            request.FileRetentionDays,
            allowed);

        server.ReportingSettingsJson = JsonSerializer.Serialize(normalized, JsonSerializerOptions.Web);

        await _configService.UpdateServerAsync(server, HttpContext.Items["Username"] as string ?? "api");
        await _syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.Configuration,
            "server-reporting-updated");
        return Ok(normalized);
    }

    [HttpGet("server/ticket-attachments")]
    public async Task<IActionResult> GetServerTicketAttachments()
    {
        var server = await _configService.GetServerConfigAsync();
        var settings = TicketAttachmentSettings.FromJson(server.TicketAttachmentSettingsJson);
        return Ok(settings);
    }

    [HttpPut("server/ticket-attachments")]
    public async Task<IActionResult> UpdateServerTicketAttachments([FromBody] TicketAttachmentSettings request)
    {
        request ??= new TicketAttachmentSettings();
        var errors = request.Validate();
        if (errors.Length > 0)
            return BadRequest(new { errors });

        var server = await _configService.GetServerConfigAsync();
        server.TicketAttachmentSettingsJson = request.ToJson();

        await _configService.UpdateServerAsync(server, HttpContext.Items["Username"] as string ?? "api");
        await _syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.Configuration,
            "server-ticket-attachments-updated");
        return Ok(TicketAttachmentSettings.FromJson(server.TicketAttachmentSettingsJson));
    }

    // ============ Server Retention ============

    [HttpGet("server/retention")]
    public async Task<IActionResult> GetServerRetention()
    {
        var server = await _configService.GetServerConfigAsync();
        var settings = RetentionSettingsHelper.DeserializeOrDefault(server.RetentionSettingsJson);
        return Ok(settings);
    }

    [HttpPut("server/retention")]
    public async Task<IActionResult> UpdateServerRetention([FromBody] UpdateRetentionRequest request)
    {
        var errors = new List<string>();

        // Validate ranges
        if (request.LogRetentionDays is < 7 or > 3650)
            errors.Add("logRetentionDays must be between 7 and 3650.");
        if (request.NotificationRetentionDays is < 7 or > 3650)
            errors.Add("notificationRetentionDays must be between 7 and 3650.");
        if (request.AgentCommandRetentionDays is < 1 or > 3650)
            errors.Add("agentCommandRetentionDays must be between 1 and 3650.");
        if (request.SessionRetentionDays is < 1 or > 3650)
            errors.Add("sessionRetentionDays must be between 1 and 3650.");
        if (request.TokenExpiredGraceDays is < 1 or > 365)
            errors.Add("tokenExpiredGraceDays must be between 1 and 365.");
        if (request.SyncPingRetentionDays is < 1 or > 365)
            errors.Add("syncPingRetentionDays must be between 1 and 365.");
        if (request.TelemetryRetentionDays is < 1 or > 365)
            errors.Add("telemetryRetentionDays must be between 1 and 365.");
        if (request.AutomationReportRetentionDays is < 1 or > 3650)
            errors.Add("automationReportRetentionDays must be between 1 and 3650.");

        if (errors.Count > 0)
            return BadRequest(new { errors });

        var server = await _configService.GetServerConfigAsync();
        var current = RetentionSettingsHelper.DeserializeOrDefault(server.RetentionSettingsJson);

        // Merge request over current
        var updated = new RetentionSettings
        {
            LogRetentionDays = request.LogRetentionDays ?? current.LogRetentionDays,
            NotificationRetentionDays = request.NotificationRetentionDays ?? current.NotificationRetentionDays,
            AgentCommandRetentionDays = request.AgentCommandRetentionDays ?? current.AgentCommandRetentionDays,
            SessionRetentionDays = request.SessionRetentionDays ?? current.SessionRetentionDays,
            TokenExpiredGraceDays = request.TokenExpiredGraceDays ?? current.TokenExpiredGraceDays,
            SyncPingRetentionDays = request.SyncPingRetentionDays ?? current.SyncPingRetentionDays,
            TelemetryRetentionDays = request.TelemetryRetentionDays ?? current.TelemetryRetentionDays,
            AutomationReportRetentionDays = request.AutomationReportRetentionDays ?? current.AutomationReportRetentionDays,
            AiChatExpiryDays = current.AiChatExpiryDays,
            AiChatGraceDays = current.AiChatGraceDays,
            DatabaseMaintenance = request.DatabaseMaintenance is not null
                ? new DatabaseMaintenanceSettings
                {
                    Enabled = request.DatabaseMaintenance.Enabled ?? current.DatabaseMaintenance?.Enabled ?? true,
                    Schedule = current.DatabaseMaintenance?.Schedule ?? "0 0 3 ? * SUN",
                    VacuumFull = request.DatabaseMaintenance.VacuumFull ?? current.DatabaseMaintenance?.VacuumFull ?? true,
                    Reindex = request.DatabaseMaintenance.Reindex ?? current.DatabaseMaintenance?.Reindex ?? true,
                    Analyze = request.DatabaseMaintenance.Analyze ?? current.DatabaseMaintenance?.Analyze ?? true,
                    VacuumAnalyze = request.DatabaseMaintenance.VacuumAnalyze ?? current.DatabaseMaintenance?.VacuumAnalyze ?? false
                }
                : current.DatabaseMaintenance
        };

        server.RetentionSettingsJson = JsonSerializer.Serialize(updated, JsonSerializerOptions.Web);

        await _configService.UpdateServerAsync(server, HttpContext.Items["Username"] as string ?? "api");
        await _syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.Configuration,
            "server-retention-updated");
        return Ok(updated);
    }

    [HttpPost("server/retention/reset")]
    public async Task<IActionResult> ResetServerRetention()
    {
        var server = await _configService.GetServerConfigAsync();
        var defaults = new RetentionSettings();
        server.RetentionSettingsJson = JsonSerializer.Serialize(defaults, JsonSerializerOptions.Web);

        await _configService.UpdateServerAsync(server, HttpContext.Items["Username"] as string ?? "api");
        await _syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.Configuration,
            "server-retention-reset");
        return Ok(defaults);
    }

    /// <summary>
    /// Triggers a manual execution of one or more maintenance jobs immediately.
    /// Jobs: data-retention, database-maintenance, log-purge, report-retention, ai-chat-retention.
    /// </summary>
    [HttpPost("server/retention/trigger")]
    public async Task<IActionResult> TriggerMaintenanceJobs([FromBody] TriggerMaintenanceRequest request)
    {
        var jobs = (request.Jobs ?? []).Select(j => j.ToLowerInvariant().Trim()).ToHashSet();
        if (jobs.Count == 0)
            return BadRequest(new { error = "At least one job must be specified. Valid values: log-purge, data-retention, report-retention, ai-chat-retention, database-maintenance." });

        var validJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "log-purge", "data-retention", "report-retention", "ai-chat-retention", "database-maintenance" };

        var invalid = jobs.Where(j => !validJobs.Contains(j)).ToList();
        if (invalid.Count > 0)
            return BadRequest(new { error = $"Invalid job(s): {string.Join(", ", invalid)}. Valid: {string.Join(", ", validJobs)}." });

        var schedulerFactory = HttpContext.RequestServices.GetRequiredService<Quartz.ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler();
        var results = new Dictionary<string, object>();

        var jobKeyMap = new Dictionary<string, Quartz.JobKey>(StringComparer.OrdinalIgnoreCase)
        {
            ["log-purge"] = Services.Quartz.LogPurgeJob.Key,
            ["data-retention"] = Services.Quartz.DataRetentionJob.Key,
            ["report-retention"] = Services.Quartz.ReportRetentionJob.Key,
            ["ai-chat-retention"] = Services.Quartz.AiChatRetentionJob.Key,
            ["database-maintenance"] = Services.Quartz.DatabaseMaintenanceJob.Key,
        };

        foreach (var jobName in jobs)
        {
            var jobKey = jobKeyMap[jobName];
            try
            {
                var jobDetail = await scheduler.GetJobDetail(jobKey);
                if (jobDetail is null)
                {
                    results[jobName] = new { status = "not-found", message = $"Job '{jobKey.Name}' is not registered in the scheduler." };
                    continue;
                }

                await scheduler.TriggerJob(jobKey);
                results[jobName] = new { status = "triggered", message = $"Job '{jobKey.Name}' triggered successfully." };
            }
            catch (Exception ex)
            {
                results[jobName] = new { status = "error", message = ex.Message };
            }
        }

        return Ok(new { message = "Maintenance jobs triggered.", results });
    }

    // ============ Client ============

    [HttpPost("server/object-storage/test")]
    public async Task<IActionResult> TestObjectStorageConnection(CancellationToken cancellationToken)
    {
        var result = await _storageFactory.TestConnectionAsync(cancellationToken);
        return Ok(result);
    }


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
        await _syncInvalidationPublisher.PublishByScopeAsync(
            SyncResourceType.Configuration,
            AppApprovalScopeType.Client,
            clientId,
            "client-configuration-updated");
        return Ok(SanitizeClientConfiguration(updated));
    }

    [HttpPatch("clients/{clientId:guid}")]
    public async Task<IActionResult> PatchClient(Guid clientId, [FromBody] Dictionary<string, object> updates)
    {
        var updated = await _configService.PatchClientAsync(clientId, updates,
            HttpContext.Items["Username"] as string ?? "api");
        await _syncInvalidationPublisher.PublishByScopeAsync(
            SyncResourceType.Configuration,
            AppApprovalScopeType.Client,
            clientId,
            "client-configuration-patched");
        return Ok(SanitizeClientConfiguration(updated));
    }

    [HttpDelete("clients/{clientId:guid}")]
    public async Task<IActionResult> DeleteClient(Guid clientId)
    {
        await _configService.DeleteClientConfigAsync(clientId,
            HttpContext.Items["Username"] as string ?? "api");
        await _syncInvalidationPublisher.PublishByScopeAsync(
            SyncResourceType.Configuration,
            AppApprovalScopeType.Client,
            clientId,
            "client-configuration-deleted");
        return NoContent();
    }

    [HttpPost("clients/{clientId:guid}/reset/{propertyName}")]
    public async Task<IActionResult> ResetClientProperty(Guid clientId, string propertyName)
    {
        try
        {
            await _configService.ResetClientPropertyAsync(clientId, propertyName,
                HttpContext.Items["Username"] as string ?? "api");
            await _syncInvalidationPublisher.PublishByScopeAsync(
                SyncResourceType.Configuration,
                AppApprovalScopeType.Client,
                clientId,
                "client-configuration-property-reset");
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
            await _syncInvalidationPublisher.PublishByScopeAsync(
                SyncResourceType.Configuration,
                AppApprovalScopeType.Site,
                siteId,
                "site-configuration-updated");
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
            await _syncInvalidationPublisher.PublishByScopeAsync(
                SyncResourceType.Configuration,
                AppApprovalScopeType.Site,
                siteId,
                "site-configuration-patched");
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
        await _syncInvalidationPublisher.PublishByScopeAsync(
            SyncResourceType.Configuration,
            AppApprovalScopeType.Site,
            siteId,
            "site-configuration-deleted");
        return NoContent();
    }

    [HttpPost("sites/{siteId:guid}/reset/{propertyName}")]
    public async Task<IActionResult> ResetSiteProperty(Guid siteId, string propertyName)
    {
        try
        {
            await _configService.ResetSitePropertyAsync(siteId, propertyName,
                HttpContext.Items["Username"] as string ?? "api");
            await _syncInvalidationPublisher.PublishByScopeAsync(
                SyncResourceType.Configuration,
                AppApprovalScopeType.Site,
                siteId,
                "site-configuration-property-reset");
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ============ AI Providers & Credentials ============

    /// <summary>Lista providers suportados</summary>
    [HttpGet("ai/providers")]
    public IActionResult GetAiProviders()
    {
        return Ok(new
        {
            providers = _aiModelCatalog.GetSupportedProviders(),
            defaults = new
            {
                openai = AIIntegrationSettings.OpenAiDefaultBaseUrl,
                openrouter = AIIntegrationSettings.OpenRouterDefaultBaseUrl,
                openai_compatible = (string?)null
            }
        });
    }

    /// <summary>Lista/obtém credenciais AI. scopeType=global|client|site</summary>
    [HttpGet("ai/credentials")]
    public async Task<IActionResult> GetAiCredentials(
        [FromQuery] string? scopeType = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        CancellationToken ct = default)
    {
        var all = await _aiCredentialRepo.GetAllAsync(ct);

        // Filtra por scope se solicitado
        if (!string.IsNullOrWhiteSpace(scopeType))
        {
            var scope = Enum.Parse<AppApprovalScopeType>(scopeType, ignoreCase: true);
            all = all.Where(c => c.ScopeType == scope).ToList();
            if (clientId.HasValue)
                all = all.Where(c => c.ClientId == clientId.Value).ToList();
            if (siteId.HasValue)
                all = all.Where(c => c.SiteId == siteId.Value).ToList();
        }

        var dtos = all.Select(c => new AiProviderCredentialDto(
            c.Id, c.ScopeType.ToString(), c.ClientId, c.SiteId, c.Provider,
            c.BaseUrl, c.EmbeddingBaseUrl, c.HasApiKey, c.HasEmbeddingApiKey,
            c.CreatedAt, c.UpdatedAt, c.CreatedBy, c.UpdatedBy
        )).ToList();

        return Ok(dtos);
    }

    /// <summary>Cria/atualiza credencial AI (upsert por scope+provider)</summary>
    [HttpPut("ai/credentials")]
    public async Task<IActionResult> UpsertAiCredential(
        [FromBody] AiProviderCredentialUpsertRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Provider))
            return BadRequest(new { error = "Provider é obrigatório." });

        var scope = Enum.Parse<AppApprovalScopeType>(request.ScopeType, ignoreCase: true);
        var username = HttpContext.Items["Username"] as string ?? "api";

        // Busca existente
        var existing = await _aiCredentialRepo.GetByScopeAsync(request.ClientId, request.SiteId, request.Provider, ct);

        if (existing is not null)
        {
            // Update
            existing.Provider = request.Provider;
            existing.BaseUrl = request.BaseUrl;
            existing.EmbeddingBaseUrl = request.EmbeddingBaseUrl;
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
                existing.ApiKeyEncrypted = _secretProtector.Protect(request.ApiKey);
            if (!string.IsNullOrWhiteSpace(request.EmbeddingApiKey))
                existing.EmbeddingApiKeyEncrypted = _secretProtector.Protect(request.EmbeddingApiKey);
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = username;

            await _aiCredentialRepo.UpdateAsync(existing, ct);

            return Ok(new AiProviderCredentialDto(
                existing.Id, existing.ScopeType.ToString(), existing.ClientId, existing.SiteId,
                existing.Provider, existing.BaseUrl, existing.EmbeddingBaseUrl,
                existing.HasApiKey, existing.HasEmbeddingApiKey,
                existing.CreatedAt, existing.UpdatedAt, existing.CreatedBy, existing.UpdatedBy
            ));
        }

        // Create
        var credential = new AiProviderCredential
        {
            ScopeType = scope,
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            Provider = request.Provider,
            BaseUrl = request.BaseUrl,
            EmbeddingBaseUrl = request.EmbeddingBaseUrl,
            ApiKeyEncrypted = string.IsNullOrWhiteSpace(request.ApiKey) ? null : _secretProtector.Protect(request.ApiKey),
            EmbeddingApiKeyEncrypted = string.IsNullOrWhiteSpace(request.EmbeddingApiKey) ? null : _secretProtector.Protect(request.EmbeddingApiKey),
            CreatedBy = username,
            UpdatedBy = username
        };

        var created = await _aiCredentialRepo.CreateAsync(credential, ct);

        return CreatedAtAction(nameof(GetAiCredentials), new { id = created.Id },
            new AiProviderCredentialDto(
                created.Id, created.ScopeType.ToString(), created.ClientId, created.SiteId,
                created.Provider, created.BaseUrl, created.EmbeddingBaseUrl,
                created.HasApiKey, created.HasEmbeddingApiKey,
                created.CreatedAt, created.UpdatedAt, created.CreatedBy, created.UpdatedBy
            ));
    }

    /// <summary>Remove credencial AI</summary>
    [HttpDelete("ai/credentials/{credentialId:guid}")]
    public async Task<IActionResult> DeleteAiCredential(Guid credentialId, CancellationToken ct)
    {
        var existing = await _aiCredentialRepo.GetByIdAsync(credentialId, ct);
        if (existing is null)
            return NotFound(new { error = $"Credential '{credentialId}' not found." });

        await _aiCredentialRepo.DeleteAsync(credentialId, ct);
        return NoContent();
    }

    /// <summary>Testa conexão com provider (valida chave, lista modelos)</summary>
    [HttpPost("ai/credentials/test")]
    public async Task<IActionResult> TestAiCredential(
        [FromBody] AiProviderCredentialUpsertRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return BadRequest(new { error = "ApiKey é obrigatória para teste." });

        try
        {
            var scope = Enum.Parse<AppApprovalScopeType>(request.ScopeType, ignoreCase: true);

            // Cria credencial temporária para teste (não persiste)
            var tempCredential = new AiProviderCredential
            {
                ScopeType = scope,
                ClientId = request.ClientId,
                SiteId = request.SiteId,
                Provider = request.Provider,
                BaseUrl = request.BaseUrl,
                EmbeddingBaseUrl = request.EmbeddingBaseUrl,
                ApiKeyEncrypted = _secretProtector.Protect(request.ApiKey),
                EmbeddingApiKeyEncrypted = !string.IsNullOrWhiteSpace(request.EmbeddingApiKey)
                    ? _secretProtector.Protect(request.EmbeddingApiKey) : null
            };

            // Tenta listar modelos como teste de conectividade
            var models = await _aiModelCatalog.ListModelsAsync(
                request.ClientId, request.SiteId,
                new AiModelSearchRequest { Provider = request.Provider, ForceRefresh = true }, ct);

            return Ok(new { success = true, modelCount = models.TotalCount, provider = request.Provider });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    // ============ AI Model Catalog ============

    /// <summary>Lista modelos disponíveis do provider</summary>
    [HttpGet("ai/models")]
    public async Task<IActionResult> ListAiModels(
        [FromQuery] string? provider = null,
        [FromQuery] string? capability = null,
        [FromQuery] string? search = null,
        [FromQuery] bool refresh = false,
        [FromQuery] bool freeOnly = false,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        CancellationToken ct = default)
    {
        var response = await _aiModelCatalog.ListModelsAsync(
            clientId, siteId,
            new AiModelSearchRequest(provider, capability, search, refresh, freeOnly),
            ct);

        return Ok(response);
    }

    /// <summary>Obtém detalhes de um modelo específico</summary>
    [HttpGet("ai/models/{modelId}")]
    public async Task<IActionResult> GetAiModel(
        string modelId,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        CancellationToken ct = default)
    {
        var model = await _aiModelCatalog.GetModelAsync(clientId, siteId, modelId, ct);
        if (model is null)
            return NotFound(new { error = $"Model '{modelId}' not found." });

        return Ok(model);
    }

    /// <summary>Valida um modelo (testa conectividade e capacidades)</summary>
    [HttpPost("ai/models/validate")]
    public async Task<IActionResult> ValidateAiModel(
        [FromBody] AiModelValidationRequest req,
        CancellationToken ct = default)
    {
        var result = await _aiModelCatalog.ValidateModelAsync(
            req.ClientId, req.SiteId, req.ModelId, req.Capability, ct);
        return Ok(result);
    }

    private static readonly string[] ManagedFields = ConfigurationFieldCatalog.ManagedFields;

    private static HashSet<string> ParseLockedFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json, JsonSerializerOptions.Web) ?? [];
            return ConfigurationFieldCatalog.NormalizeFieldSet(values);
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
        var agentUpdate = DeserializeOrDefault<AgentUpdatePolicy>(client?.AgentUpdatePolicyJson)
            ?? DeserializeOrDefault<AgentUpdatePolicy>(server.AgentUpdatePolicyJson)
            ?? new AgentUpdatePolicy();
        agentUpdate.Normalize();

        var ai = DeserializeOrDefault<AIIntegrationSettings>(client?.AIIntegrationSettingsJson)
            ?? DeserializeOrDefault<AIIntegrationSettings>(server.AIIntegrationSettingsJson)
            ?? new AIIntegrationSettings();
        ai.ApiKey = null;
        ai.EmbeddingApiKey = null;

        return new
        {
            ClientId = clientId,
            RecoveryEnabled = client?.RecoveryEnabled ?? server.RecoveryEnabled,
            DiscoveryEnabled = client?.DiscoveryEnabled ?? server.DiscoveryEnabled,
            P2PFilesEnabled = client?.P2PFilesEnabled ?? server.P2PFilesEnabled,
            CloudBootstrapEnabled = client?.CloudBootstrapEnabled ?? server.CloudBootstrapEnabled,
            SupportEnabled = client?.SupportEnabled ?? server.SupportEnabled,
            MeshCentralGroupPolicyProfile = string.IsNullOrWhiteSpace(client?.MeshCentralGroupPolicyProfile)
                ? server.MeshCentralGroupPolicyProfile
                : client.MeshCentralGroupPolicyProfile,
            ChatAIEnabled = client?.ChatAIEnabled ?? server.ChatAIEnabled,
            KnowledgeBaseEnabled = client?.KnowledgeBaseEnabled ?? server.KnowledgeBaseEnabled,
            AppStorePolicy = client?.AppStorePolicy ?? server.AppStorePolicy,
            InventoryIntervalHours = client?.InventoryIntervalHours ?? server.InventoryIntervalHours,
            AgentHeartbeatIntervalSeconds = client?.AgentHeartbeatIntervalSeconds ?? server.AgentHeartbeatIntervalSeconds,
            AgentOnlineGraceSeconds = client?.AgentOnlineGraceSeconds ?? server.AgentOnlineGraceSeconds,
            AutoUpdate = autoUpdate,
            AgentUpdate = agentUpdate,
            AIIntegration = ai,
            HasLocalConfiguration = client is not null,
            BlockedFields = blocked.OrderBy(x => x).ToArray(),
            Inheritance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["RecoveryEnabled"] = client?.RecoveryEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["DiscoveryEnabled"] = client?.DiscoveryEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["P2PFilesEnabled"] = client?.P2PFilesEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["CloudBootstrapEnabled"] = client?.CloudBootstrapEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["SupportEnabled"] = client?.SupportEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["MeshCentralGroupPolicyProfile"] = !string.IsNullOrWhiteSpace(client?.MeshCentralGroupPolicyProfile)
                    ? (int)ConfigurationPriorityType.Client
                    : (int)ConfigurationPriorityType.Global,
                ["ChatAIEnabled"] = client?.ChatAIEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["KnowledgeBaseEnabled"] = client?.KnowledgeBaseEnabled is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["AppStorePolicy"] = client?.AppStorePolicy is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["InventoryIntervalHours"] = client?.InventoryIntervalHours is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["AgentHeartbeatIntervalSeconds"] = client?.AgentHeartbeatIntervalSeconds is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["AgentOnlineGraceSeconds"] = client?.AgentOnlineGraceSeconds is not null ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["AutoUpdate"] = !string.IsNullOrWhiteSpace(client?.AutoUpdateSettingsJson) ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
                ["AgentUpdate"] = !string.IsNullOrWhiteSpace(client?.AgentUpdatePolicyJson) ? (int)ConfigurationPriorityType.Client : (int)ConfigurationPriorityType.Global,
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
        {
            config.AIIntegration.ApiKey = null;
            config.AIIntegration.EmbeddingApiKey = null;
        }
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
            ai.EmbeddingApiKey = null;
            return JsonSerializer.Serialize(ai, JsonSerializerOptions.Web);
        }
        catch
        {
            return json;
        }
    }

    public sealed record NatsConnectionTestRequest
    {
        public string? Url { get; init; }
        public string? User { get; init; }
        public string? Password { get; init; }
    }

    /// <summary>
    /// Campos NATS configuráveis pelo usuário.
    /// Todos opcionais — apenas campos informados são alterados.
    /// TTL aceito: mínimo 15 minutos, máximo 4320 minutos (72h), padrão 1440 minutos (24h).
    /// </summary>
    public sealed record NatsSettingsRequest(
        bool? NatsEnabled = null,
        bool? NatsAuthEnabled = null,
        bool? NatsUseWssExternal = null,
        string? NatsServerHostInternal = null,
        string? NatsServerHostExternal = null,
        int? NatsAgentJwtTtlMinutes = null,
        int? NatsUserJwtTtlMinutes = null);

    private static ReportingSettingsResponse BuildDefaultReporting(ReportingOptions options)
    {
        var allowed = NormalizeAllowedDays(options.AllowedRetentionDays);
        var dbDays = allowed.Contains(options.DatabaseRetentionDays) ? options.DatabaseRetentionDays : allowed.Last();
        var fileDays = allowed.Contains(options.FileRetentionDays) ? options.FileRetentionDays : allowed.Last();

        return new ReportingSettingsResponse(
            options.EnablePdf,
            Math.Clamp(options.ProcessingTimeoutSeconds, 30, 3600),
            Math.Clamp(options.FileDownloadTimeoutSeconds, 5, 600),
            Math.Clamp(options.MaxConcurrentExecutions, 1, 16),
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
                ProcessingTimeoutSeconds = Math.Clamp(parsed.ProcessingTimeoutSeconds, 30, 3600),
                FileDownloadTimeoutSeconds = Math.Clamp(parsed.FileDownloadTimeoutSeconds, 5, 600),
                MaxConcurrentExecutions = Math.Clamp(parsed.MaxConcurrentExecutions, 1, 16),
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
    int MaxConcurrentExecutions,
    int DatabaseRetentionDays,
    int FileRetentionDays,
    int[]? AllowedRetentionDays);

public record ReportingSettingsResponse(
    bool EnablePdf,
    int ProcessingTimeoutSeconds,
    int FileDownloadTimeoutSeconds,
    int MaxConcurrentExecutions,
    int DatabaseRetentionDays,
    int FileRetentionDays,
    int[] AllowedRetentionDays);

public record UpdateRetentionRequest(
    int? LogRetentionDays = null,
    int? NotificationRetentionDays = null,
    int? AgentCommandRetentionDays = null,
    int? SessionRetentionDays = null,
    int? TokenExpiredGraceDays = null,
    int? SyncPingRetentionDays = null,
    int? TelemetryRetentionDays = null,
    int? AutomationReportRetentionDays = null,
    UpdateDatabaseMaintenanceRequest? DatabaseMaintenance = null);

public record UpdateDatabaseMaintenanceRequest(
    bool? Enabled = null,
    bool? VacuumFull = null,
    bool? Reindex = null,
    bool? Analyze = null,
    bool? VacuumAnalyze = null);

public record TriggerMaintenanceRequest(
    string[]? Jobs = null);

internal static class RetentionSettingsHelper
{
    public static RetentionSettings DeserializeOrDefault(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new RetentionSettings();

        try
        {
            return JsonSerializer.Deserialize<RetentionSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new RetentionSettings();
        }
        catch
        {
            return new RetentionSettings();
        }
    }
}

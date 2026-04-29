using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Controllers;

/// <summary>
/// Base partial class for agent-authenticated endpoints (/api/agent-auth/*).
/// Contains shared DI, auth helpers, and utility methods used by all partial files.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/agent-auth")]
[AllowAnonymous]
public partial class AgentAuthController : ControllerBase
{
    // ── Repositories ──────────────────────────────────────────────────────
    private readonly IAgentRepository _agentRepo;
    private readonly IAgentHardwareRepository _hardwareRepo;
    private readonly IAgentSoftwareRepository _softwareRepo;
    private readonly ICommandRepository _commandRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IClientRepository _clientRepo;
    private readonly ITicketRepository _ticketRepo;
    private readonly IWorkflowRepository _workflowRepo;
    private readonly IWorkflowProfileRepository _workflowProfileRepo;
    private readonly IKnowledgeArticleRepository _knowledgeRepo;
    private readonly IP2pBootstrapRepository _p2pBootstrapRepo;
    private readonly ISyncPingDeliveryRepository _syncPingDeliveryRepository;
    private readonly IAutomationExecutionReportRepository _automationExecutionReportRepository;
    private readonly IAgentMonitoringEventRepository _monitoringEventRepository;
    private readonly IAgentLabelRepository _agentLabelRepository;

    // ── Domain Services ───────────────────────────────────────────────────
    private readonly IAgentAutoLabelingService _agentAutoLabelingService;
    private readonly IAgentUpdateService _agentUpdateService;
    private readonly IAiChatService _aiChatService;
    private readonly IAppStoreService _appStoreService;
    private readonly IActivityLogService _activityLogService;
    private readonly ISlaService _slaService;
    private readonly IAutomationTaskService _automationTaskService;
    private readonly IAutoTicketOrchestratorService _autoTicketOrchestratorService;
    private readonly IMonitoringEventNormalizationService _monitoringEventNormalizationService;
    private readonly ICustomFieldService _customFieldService;
    private readonly IDeployTokenService _deployTokenService;

    // ── Configuration & Infrastructure ────────────────────────────────────
    private readonly IConfigurationResolver _configResolver;
    private readonly IConfigurationService _configService;
    private readonly IRedisService _redisService;
    private readonly INatsCredentialsService _natsCredentialsService;
    private readonly IMeshCentralEmbeddingService _meshCentralEmbeddingService;
    private readonly IMeshCentralApiService _meshCentralApiService;
    private readonly IMeshCentralProvisioningService _meshCentralProvisioningService;
    private readonly IAgentTlsCertificateProbe _tlsCertProbe;
    private readonly MeshCentralOptions _meshCentralOptions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentAuthController> _logger;

    public AgentAuthController(
        IAgentRepository agentRepo,
        IAgentHardwareRepository hardwareRepo,
        IAgentSoftwareRepository softwareRepo,
        ICommandRepository commandRepo,
        IConfigurationResolver configResolver,
        IConfigurationService configService,
        ITicketRepository ticketRepo,
        IWorkflowRepository workflowRepo,
        IWorkflowProfileRepository workflowProfileRepo,
        ISiteRepository siteRepo,
        ISlaService slaService,
        IActivityLogService activityLogService,
        IAiChatService aiChatService,
        IAppStoreService appStoreService,
        IKnowledgeArticleRepository knowledgeRepo,
        IAgentAutoLabelingService agentAutoLabelingService,
        IAgentLabelRepository agentLabelRepository,
        IAutomationTaskService automationTaskService,
        IAutomationExecutionReportRepository automationExecutionReportRepository,
        IAgentMonitoringEventRepository monitoringEventRepository,
        IAutoTicketOrchestratorService autoTicketOrchestratorService,
        IMonitoringEventNormalizationService monitoringEventNormalizationService,
        ISyncPingDeliveryRepository syncPingDeliveryRepository,
        INatsCredentialsService natsCredentialsService,
        IMeshCentralEmbeddingService meshCentralEmbeddingService,
        IMeshCentralApiService meshCentralApiService,
        IMeshCentralProvisioningService meshCentralProvisioningService,
        IClientRepository clientRepo,
        IDeployTokenService deployTokenService,
        IRedisService redisService,
        IP2pBootstrapRepository p2pBootstrapRepo,
        IAgentUpdateService agentUpdateService,
        ICustomFieldService customFieldService,
        IOptions<MeshCentralOptions> meshCentralOptions,
        IAgentTlsCertificateProbe tlsCertProbe,
        IConfiguration configuration,
        ILogger<AgentAuthController> logger)
    {
        _agentRepo = agentRepo;
        _hardwareRepo = hardwareRepo;
        _softwareRepo = softwareRepo;
        _commandRepo = commandRepo;
        _configResolver = configResolver;
        _configService = configService;
        _ticketRepo = ticketRepo;
        _workflowRepo = workflowRepo;
        _workflowProfileRepo = workflowProfileRepo;
        _siteRepo = siteRepo;
        _slaService = slaService;
        _activityLogService = activityLogService;
        _aiChatService = aiChatService;
        _appStoreService = appStoreService;
        _knowledgeRepo = knowledgeRepo;
        _agentAutoLabelingService = agentAutoLabelingService;
        _agentLabelRepository = agentLabelRepository;
        _automationTaskService = automationTaskService;
        _automationExecutionReportRepository = automationExecutionReportRepository;
        _monitoringEventRepository = monitoringEventRepository;
        _autoTicketOrchestratorService = autoTicketOrchestratorService;
        _monitoringEventNormalizationService = monitoringEventNormalizationService;
        _syncPingDeliveryRepository = syncPingDeliveryRepository;
        _natsCredentialsService = natsCredentialsService;
        _meshCentralEmbeddingService = meshCentralEmbeddingService;
        _meshCentralApiService = meshCentralApiService;
        _meshCentralProvisioningService = meshCentralProvisioningService;
        _clientRepo = clientRepo;
        _deployTokenService = deployTokenService;
        _redisService = redisService;
        _p2pBootstrapRepo = p2pBootstrapRepo;
        _agentUpdateService = agentUpdateService;
        _customFieldService = customFieldService;
        _meshCentralOptions = meshCentralOptions.Value;
        _tlsCertProbe = tlsCertProbe;
        _configuration = configuration;
        _logger = logger;
    }

    // ── Shared Auth Helpers ───────────────────────────────────────────────

    /// <summary>
    /// Retrieves the authenticated agent ID from the HTTP context items (set by AgentAuthMiddleware).
    /// </summary>
    private bool TryGetAuthenticatedAgentId(out Guid agentId)
    {
        if (HttpContext.Items["AgentId"] is Guid id)
        {
            agentId = id;
            return true;
        }

        agentId = Guid.Empty;
        return false;
    }

    /// <summary>
    /// Validates the agent exists and optionally blocks pending (zero-touch) agents.
    /// Returns (agent, errorResult) tuple where errorResult is non-null when the request should be blocked.
    /// </summary>
    private async Task<(Agent? agent, IActionResult? blocked)> GetAgentOrBlockPendingAsync(Guid agentId, bool allowPending)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return (null, NotFound(new { error = "Agent not found." }));

        if (!allowPending && agent.ZeroTouchPending)
            return (null, StatusCode(403, new { error = "Agent registration is pending (zero-touch)." }));

        return (agent, null);
    }

    /// <summary>
    /// Invalidates all agent-level inventory caches in Redis.
    /// </summary>
    private async Task InvalidateAgentInventoryCachesAsync(Guid agentId)
    {
        try
        {
            var site = await _siteRepo.GetByIdAsync((await _agentRepo.GetByIdAsync(agentId))!.SiteId);
            if (site is not null)
            {
                var allKey = $"inventory:catalog:client:{site.ClientId}";
                var swKey = $"inventory:software:agent:{agentId}";
                await Task.WhenAll(
                    _redisService.DeleteAsync(allKey),
                    _redisService.DeleteAsync(swKey));
            }
        }
        catch
        {
            // Best-effort cache invalidation; failures are non-critical.
        }
    }

    // ── NATS TLS Helpers ──────────────────────────────────────────────────

    private static string? BuildNatsProbeUrl(string? natsHost)
    {
        if (string.IsNullOrWhiteSpace(natsHost))
            return null;

        var colonIdx = natsHost.LastIndexOf(':');
        var host = colonIdx > 0 ? natsHost[..colonIdx] : natsHost;
        return $"https://{host}:443";
    }

    private static string? BuildNatsCacheKey(string? natsHost)
    {
        if (string.IsNullOrWhiteSpace(natsHost))
            return null;

        var colonIdx = natsHost.LastIndexOf(':');
        var host = colonIdx > 0 ? natsHost[..colonIdx] : natsHost;
        return $"nats-tls:{host}:443";
    }

    // ── App Store Helpers ─────────────────────────────────────────────────

    private static string ComputeAppStoreRevision(IReadOnlyCollection<object> apps)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(",", apps.Select(a => a.GetHashCode())))));

    // ── Automation / Monitoring Helpers ───────────────────────────────────

    private AgentMonitoringEvent? TryBuildMonitoringEventFromAutomationResult(
        Guid clientId,
        Guid siteId,
        Guid agentId,
        Guid? sourceRefId,
        string? correlationId,
        AutomationExecutionResultRequest request,
        IEnumerable<string> labels)
    {
        if (string.IsNullOrWhiteSpace(request.MetadataJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(request.MetadataJson);
            var root = document.RootElement;
            var alertCode = ParseJson.GetStringProperty(root, "alert_code") ?? ParseJson.GetStringProperty(root, "alertCode");
            if (string.IsNullOrWhiteSpace(alertCode))
                return null;

            var severity = TryResolveMonitoringSeverity(root, out var parsedSeverity)
                ? parsedSeverity
                : request.Success
                    ? MonitoringEventSeverity.Warning
                    : MonitoringEventSeverity.Critical;

            var metricKey = ParseJson.GetStringProperty(root, "metric_key") ?? ParseJson.GetStringProperty(root, "metricKey");
            var metricValue = ParseJson.GetNullableDecimal(root, "metric_value") ?? ParseJson.GetNullableDecimal(root, "metricValue");
            var occurredAt = ParseJson.GetNullableDateTime(root, "occurred_at") ?? ParseJson.GetNullableDateTime(root, "occurredAt") ?? DateTime.UtcNow;
            var title = ParseJson.GetStringProperty(root, "title") ?? $"[{severity}] {alertCode}";
            var message = ParseJson.GetStringProperty(root, "message")
                ?? BuildMonitoringEventMessage(alertCode, metricKey, metricValue, request.Success, request.ExitCode, request.ErrorMessage);

            return new AgentMonitoringEvent
            {
                ClientId = clientId,
                SiteId = siteId,
                AgentId = agentId,
                AlertCode = alertCode.Trim(),
                Severity = severity,
                Title = title,
                Message = message,
                MetricKey = metricKey,
                MetricValue = metricValue,
                PayloadJson = request.MetadataJson,
                LabelsSnapshotJson = _monitoringEventNormalizationService.SerializeLabels(labels.ToArray()),
                Source = MonitoringEventSource.Automation,
                SourceRefId = sourceRefId,
                CorrelationId = correlationId,
                OccurredAt = occurredAt
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryResolveMonitoringSeverity(JsonElement obj, out MonitoringEventSeverity severity)
    {
        var rawSeverity = ParseJson.GetStringProperty(obj, "severity") ?? ParseJson.GetStringProperty(obj, "severity_level");
        if (string.IsNullOrWhiteSpace(rawSeverity))
        {
            severity = MonitoringEventSeverity.Warning;
            return false;
        }

        if (int.TryParse(rawSeverity, out var numericSeverity) && Enum.IsDefined(typeof(MonitoringEventSeverity), numericSeverity))
        {
            severity = (MonitoringEventSeverity)numericSeverity;
            return true;
        }

        severity = rawSeverity.Trim().ToLowerInvariant() switch
        {
            "attention" or "low" => MonitoringEventSeverity.Attention,
            "warning" or "medium" => MonitoringEventSeverity.Warning,
            "critical" or "high" or "error" => MonitoringEventSeverity.Critical,
            _ => MonitoringEventSeverity.Warning
        };

        return true;
    }

    private static string BuildMonitoringEventMessage(
        string alertCode,
        string? metricKey,
        decimal? metricValue,
        bool success,
        int? exitCode,
        string? errorMessage)
    {
        if (!string.IsNullOrWhiteSpace(metricKey) && metricValue.HasValue)
            return $"Automation generated alert '{alertCode}' from metric {metricKey}={metricValue.Value}.";

        if (!success && !string.IsNullOrWhiteSpace(errorMessage))
            return $"Automation generated alert '{alertCode}' after execution failure: {errorMessage}.";

        if (exitCode.HasValue)
            return $"Automation generated alert '{alertCode}' with exit code {exitCode.Value}.";

        return $"Automation generated monitoring alert '{alertCode}'.";
    }
}

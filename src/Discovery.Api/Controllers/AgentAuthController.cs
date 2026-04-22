using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/agent-auth")]
[AllowAnonymous]
public class AgentAuthController : ControllerBase
{
    private readonly IAgentRepository _agentRepo;
    private readonly IAgentHardwareRepository _hardwareRepo;
    private readonly IAgentSoftwareRepository _softwareRepo;
    private readonly ICommandRepository _commandRepo;
    private readonly IConfigurationResolver _configResolver;
    private readonly IConfigurationService _configService;
    private readonly ITicketRepository _ticketRepo;
    private readonly IWorkflowRepository _workflowRepo;
    private readonly IWorkflowProfileRepository _workflowProfileRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly ISlaService _slaService;
    private readonly IActivityLogService _activityLogService;
    private readonly IAiChatService _aiChatService;
    private readonly IAppStoreService _appStoreService;
    private readonly IKnowledgeArticleRepository _knowledgeRepo;
    private readonly IAgentAutoLabelingService _agentAutoLabelingService;
    private readonly IAgentLabelRepository _agentLabelRepository;
    private readonly IAutomationTaskService _automationTaskService;
    private readonly IAutomationExecutionReportRepository _automationExecutionReportRepository;
    private readonly IAgentMonitoringEventRepository _monitoringEventRepository;
    private readonly IAutoTicketOrchestratorService _autoTicketOrchestratorService;
    private readonly IMonitoringEventNormalizationService _monitoringEventNormalizationService;
    private readonly ISyncPingDeliveryRepository _syncPingDeliveryRepository;
    private readonly INatsCredentialsService _natsCredentialsService;
    private readonly IMeshCentralEmbeddingService _meshCentralEmbeddingService;
    private readonly IMeshCentralApiService _meshCentralApiService;
    private readonly IMeshCentralProvisioningService _meshCentralProvisioningService;
    private readonly IClientRepository _clientRepo;
    private readonly IDeployTokenService _deployTokenService;
    private readonly IRedisService _redisService;
    private readonly IP2pBootstrapRepository _p2pBootstrapRepo;
    private readonly IAgentUpdateService _agentUpdateService;
    private readonly ICustomFieldService _customFieldService;
    private readonly MeshCentralOptions _meshCentralOptions;
    private readonly IAgentTlsCertificateProbe _tlsCertProbe;
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

    /// <summary>
    /// Retorna a configuração efetiva do agent (hierarquia resolvida: Server → Client → Site).
    /// Usada pelo agent para saber seu intervalo de inventário, features habilitadas, etc.
    /// </summary>
    [HttpGet("me/configuration")]
    public async Task<IActionResult> GetConfiguration()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: true);
        if (blocked is not null)
            return blocked;

        if (agent!.ZeroTouchPending)
            return Ok(new { zeroTouchPending = true });

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.SupportEnabled;
        if (resolved.AIIntegration is not null)
            resolved.AIIntegration.ApiKey = null;

        var serverConfig = await _configService.GetServerConfigAsync();

        // Flags anti-MITM: informam o agent o que o servidor espera na conexão.
        var enforceTls = _configuration.GetValue<bool>("Security:AgentConnection:EnforceTlsHashValidation");
        var handshakeEnabled = _configuration.GetValue<bool>("Security:AgentConnection:HandshakeEnabled");

        // Hash TLS do endpoint da API (SignalR) — agent usa para validar a conexão WebSocket.
        string? apiTlsCertHash = null;
        if (enforceTls || handshakeEnabled)
            apiTlsCertHash = await _tlsCertProbe.GetExpectedTlsCertHashAsync(HttpContext.RequestAborted);

        // Hash TLS do broker NATS WSS (quando configurado) — agent usa para TLS pinning no NATS.
        string? natsTlsCertHash = null;
        var natsHost = string.IsNullOrWhiteSpace(serverConfig.NatsServerHostExternal)
            ? serverConfig.NatsServerHostInternal
            : serverConfig.NatsServerHostExternal;
        var natsProbeUrl = BuildNatsProbeUrl(natsHost);
        var natsCacheKey = BuildNatsCacheKey(natsHost);
        if (serverConfig.NatsUseWssExternal && !string.IsNullOrWhiteSpace(natsProbeUrl) && !string.IsNullOrWhiteSpace(natsCacheKey))
            natsTlsCertHash = await _tlsCertProbe.GetExpectedTlsCertHashAsync(natsProbeUrl, natsCacheKey, HttpContext.RequestAborted);

        // Payload enxuto para agent: sem metadados de heranca/bloqueio e com flag booleana de App Store.
        return Ok(new
        {
            resolved.RecoveryEnabled,
            resolved.DiscoveryEnabled,
            resolved.P2PFilesEnabled,
            resolved.SupportEnabled,
            MeshCentralEnabledEffective = meshCentralEnabledEffective,
            resolved.ChatAIEnabled,
            resolved.KnowledgeBaseEnabled,
            AppStoreEnabled = resolved.AppStorePolicy != AppStorePolicyType.Disabled,
            resolved.InventoryIntervalHours,
            resolved.AutoUpdate,
            resolved.AgentUpdate,
            resolved.AgentHeartbeatIntervalSeconds,
            resolved.AgentOnlineGraceSeconds,
            resolved.SiteId,
            resolved.ClientId,
            resolved.ResolvedAt,
            NatsServerHost = natsHost,
            NatsUseWssExternal = serverConfig.NatsUseWssExternal,
            // Campos anti-MITM: flags e hashes TLS para que o agent saiba o que validar.
            EnforceTlsHashValidation = enforceTls,
            HandshakeEnabled = handshakeEnabled,
            ApiTlsCertHash = apiTlsCertHash,
            NatsTlsCertHash = natsTlsCertHash
        });
    }

    /// <summary>
    /// Reporta mismatch de hash TLS observado pelo agent.
    /// Serve para invalidar cache e forçar novo probe no servidor.
    /// </summary>
    [HttpPost("me/tls-mismatch")]
    public async Task<IActionResult> ReportTlsMismatch([FromBody] AgentTlsMismatchReport? report, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out _))
            return Unauthorized(new { error = "Agent not authenticated." });

        if (report is null || string.IsNullOrWhiteSpace(report.Target))
            return BadRequest(new { error = "Target is required." });

        var target = report.Target.Trim().ToLowerInvariant();
        if (target != "api" && target != "nats")
            return BadRequest(new { error = "Target must be 'api' or 'nats'." });

        string? newHash = null;
        if (target == "api")
        {
            _tlsCertProbe.InvalidateCache(AgentTlsCertificateProbe.ApiCacheKey);
            newHash = await _tlsCertProbe.GetExpectedTlsCertHashAsync(ct);
        }
        else
        {
            var serverConfig = await _configService.GetServerConfigAsync();
            var natsHost = string.IsNullOrWhiteSpace(serverConfig.NatsServerHostExternal)
                ? serverConfig.NatsServerHostInternal
                : serverConfig.NatsServerHostExternal;
            var natsProbeUrl = BuildNatsProbeUrl(natsHost);
            var natsCacheKey = BuildNatsCacheKey(natsHost);
            if (!string.IsNullOrWhiteSpace(natsCacheKey))
                _tlsCertProbe.InvalidateCache(natsCacheKey);
            if (!string.IsNullOrWhiteSpace(natsProbeUrl) && !string.IsNullOrWhiteSpace(natsCacheKey))
                newHash = await _tlsCertProbe.GetExpectedTlsCertHashAsync(natsProbeUrl, natsCacheKey, ct);
        }

        return Ok(new { expectedHash = newHash });
    }

    /// <summary>
    /// Gera URL de embedding do MeshCentral para o agent autenticado.
    /// O token de auth e gerado no backend para evitar exposicao de segredos no front/agent.
    /// </summary>
    [HttpPost("me/support/meshcentral/embed-url")]
    public async Task<IActionResult> CreateMeshCentralEmbedUrl([FromBody] AgentMeshCentralEmbedRequest? request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.SupportEnabled;
        if (!meshCentralEnabledEffective)
            return StatusCode(403, new { error = "MeshCentral support is disabled for this scope." });

        var desiredViewMode = request?.ViewMode ?? 11;
        var effectiveMeshNodeId = string.IsNullOrWhiteSpace(agent.MeshCentralNodeId)
            ? request?.MeshNodeId
            : agent.MeshCentralNodeId;

        try
        {
            var embed = await _meshCentralEmbeddingService.GenerateAgentEmbedUrlAsync(
                agent,
                site.ClientId,
                desiredViewMode,
                request?.HideMask,
                effectiveMeshNodeId,
                request?.GotoDeviceName,
                HttpContext.RequestAborted);

            return Ok(new
            {
                url = embed.Url,
                expiresAtUtc = embed.ExpiresAtUtc,
                viewMode = embed.ViewMode,
                hideMask = embed.HideMask,
                agentId = agent.Id,
                meshNodeId = effectiveMeshNodeId,
                clientId = site.ClientId,
                siteId = site.Id
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Retorna as instruções de instalação do agente MeshCentral para o agent autenticado.
    /// Não requer deploy token — o agent usa seu próprio Bearer token para autenticação.
    /// </summary>
    [HttpGet("me/support/meshcentral/install")]
    public async Task<IActionResult> GetMeshCentralInstall()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.SupportEnabled;
        if (!meshCentralEnabledEffective)
            return StatusCode(403, new { error = "MeshCentral support is disabled for this scope." });

        var client = await _clientRepo.GetByIdAsync(site.ClientId);
        if (client is null)
            return NotFound(new { error = "Client not found." });

        try
        {
            var instructions = await _meshCentralApiService.ProvisionInstallAsync(client, site, string.Empty, HttpContext.RequestAborted);
            return Ok(instructions);
        }
        catch (InvalidOperationException)
        {
            try
            {
                var fallback = await _meshCentralProvisioningService.BuildInstallInstructionsAsync(
                    client,
                    site,
                    string.Empty,
                    meshCentralEnabledEffective);
                return Ok(fallback);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(503, new { error = ex.Message });
            }
        }
    }

    [HttpGet("me/app-store")]
    public async Task<IActionResult> GetAppStoreEffective(
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return NotFound(new { error = "Site not found for this agent." });

        var effective = await _appStoreService.GetEffectiveAppsAsync(
            site.ClientId,
            site.Id,
            agent.Id,
            installationType,
            cancellationToken);

        return Ok(new
        {
            installationType,
            count = effective.Count,
            items = effective
        });
    }

    [HttpGet("me/sync-manifest")]
    public async Task<IActionResult> GetSyncManifest(CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return NotFound(new { error = "Site not found for this agent." });

        var serverConfig = await _configService.GetServerConfigAsync();
        var clientConfig = await _configService.GetClientConfigAsync(site.ClientId);
        var siteConfig = await _configService.GetSiteConfigAsync(site.Id);
        var automationFingerprint = await _automationTaskService.GetPolicyFingerprintForAgentAsync(agentId, cancellationToken);

        var wingetApps = await _appStoreService.GetEffectiveAppsAsync(
            site.ClientId,
            site.Id,
            agent.Id,
            AppInstallationType.Winget,
            cancellationToken);

        var chocolateyApps = await _appStoreService.GetEffectiveAppsAsync(
            site.ClientId,
            site.Id,
            agent.Id,
            AppInstallationType.Chocolatey,
            cancellationToken);

        var customApps = await _appStoreService.GetEffectiveAppsAsync(
            site.ClientId,
            site.Id,
            agent.Id,
            AppInstallationType.Custom,
            cancellationToken);
        var agentUpdateManifest = await _agentUpdateService.GetManifestAsync(
            agentId,
            new AgentUpdateManifestRequest(
                agent.AgentVersion,
                null,
                null,
                null),
            cancellationToken);

        var resources = new List<AgentSyncManifestResourceDto>
        {
            new()
            {
                Resource = SyncResourceType.Configuration,
                Revision = $"cfg:{serverConfig.Version}:{clientConfig?.Version ?? 0}:{siteConfig?.Version ?? 0}",
                RecommendedSyncInSeconds = 300,
                Endpoint = "/api/agent-auth/me/configuration"
            },
            new()
            {
                Resource = SyncResourceType.AutomationPolicy,
                Revision = $"automation:{automationFingerprint}",
                RecommendedSyncInSeconds = 180,
                Endpoint = "/api/agent-auth/me/automation/policy-sync",
            },
            new()
            {
                Resource = SyncResourceType.AppStore,
                Variant = AppInstallationType.Winget.ToString(),
                Revision = $"app-store:winget:{ComputeAppStoreRevision(wingetApps)}",
                RecommendedSyncInSeconds = 900,
                Endpoint = "/api/agent-auth/me/app-store?installationType=Winget"
            },
            new()
            {
                Resource = SyncResourceType.AppStore,
                Variant = AppInstallationType.Chocolatey.ToString(),
                Revision = $"app-store:chocolatey:{ComputeAppStoreRevision(chocolateyApps)}",
                RecommendedSyncInSeconds = 900,
                Endpoint = "/api/agent-auth/me/app-store?installationType=Chocolatey"
            },
            new()
            {
                Resource = SyncResourceType.AppStore,
                Variant = AppInstallationType.Custom.ToString(),
                Revision = $"app-store:custom:{ComputeAppStoreRevision(customApps)}",
                RecommendedSyncInSeconds = 900,
                Endpoint = "/api/agent-auth/me/app-store?installationType=Custom"
            },
            new()
            {
                Resource = SyncResourceType.AgentUpdate,
                Revision = agentUpdateManifest.Revision,
                RecommendedSyncInSeconds = 600,
                Endpoint = "/api/agent-auth/me/update/manifest"
            }
        };

        var manifest = new AgentSyncManifestDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            RecommendedPollSeconds = 900,
            MaxStaleSeconds = 86400,
            Resources = resources
        };

        return Ok(manifest);
    }

    [HttpGet("me/update/manifest")]
    public async Task<IActionResult> GetAgentUpdateManifest(
        [FromQuery] string? currentVersion = null,
        [FromQuery] string? platform = null,
        [FromQuery] string? architecture = null,
        [FromQuery] AgentReleaseArtifactType? artifactType = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        try
        {
            var manifest = await _agentUpdateService.GetManifestAsync(
                agentId,
                new AgentUpdateManifestRequest(currentVersion, platform, architecture, artifactType),
                cancellationToken);
            return Ok(manifest);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("me/update/download")]
    public async Task<IActionResult> DownloadAgentUpdate(
        [FromQuery] Guid? releaseId = null,
        [FromQuery] string? version = null,
        [FromQuery] string? platform = null,
        [FromQuery] string? architecture = null,
        [FromQuery] AgentReleaseArtifactType? artifactType = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        try
        {
            var payload = await _agentUpdateService.GetPresignedDownloadUrlAsync(
                agentId,
                new AgentUpdateDownloadRequest(releaseId, version, platform, architecture, artifactType),
                cancellationToken);

            if (payload is null)
                return NotFound(new { error = "No applicable update artifact is available for this agent." });

            Response.Headers["X-Agent-Update-Sha256"] = payload.Sha256;
            Response.Headers["X-Agent-Update-Platform"] = payload.Platform;
            Response.Headers["X-Agent-Update-Architecture"] = payload.Architecture;
            Response.Headers["X-Agent-Update-ArtifactType"] = payload.ArtifactType.ToString();

            return Redirect(payload.DownloadUrl);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("me/update/report")]
    public async Task<IActionResult> ReportAgentUpdate(
        [FromBody] AgentUpdateReportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        try
        {
            var updateEvent = await _agentUpdateService.RecordEventAsync(agentId, request, cancellationToken);
            return Ok(updateEvent);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("me/sync/ping/{eventId:guid}/ack")]
    public async Task<IActionResult> AckSyncPing(
        Guid eventId,
        [FromBody] SyncPingAckRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        if (string.IsNullOrWhiteSpace(request.Revision))
            return BadRequest(new { error = "Revision is required." });

        var acknowledgedAt = request.ProcessedAt ?? DateTime.UtcNow;
        var delivery = await _syncPingDeliveryRepository.UpsertAckAsync(eventId, agentId, request, acknowledgedAt);

        return Ok(new SyncPingAckResponse
        {
            Acknowledged = true,
            EventId = eventId,
            DeliveryId = delivery.Id
        });
    }

    [HttpPost("me/automation/policy-sync")]
    public async Task<IActionResult> SyncAutomationPolicy(
        [FromBody] AgentAutomationPolicySyncRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var correlationId = Request.Headers.TryGetValue("X-Correlation-Id", out var values)
            && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : Guid.NewGuid().ToString("N");

        var response = await _automationTaskService.SyncPolicyForAgentAsync(
            agentId,
            request ?? new AgentAutomationPolicySyncRequest(),
            HttpContext.Items["Username"] as string ?? "agent",
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            correlationId,
            cancellationToken);

        Response.Headers["X-Correlation-Id"] = correlationId;
        return Ok(response);
    }

    [HttpGet("me/custom-fields/runtime")]
    public async Task<IActionResult> GetRuntimeCustomFields(
        [FromQuery] Guid? taskId = null,
        [FromQuery] Guid? scriptId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var values = await _customFieldService.GetRuntimeValuesForAgentAsync(agentId, taskId, scriptId, cancellationToken);
        return Ok(values);
    }

    [HttpPost("me/custom-fields/collected")]
    public async Task<IActionResult> UpsertCollectedCustomField(
        [FromBody] AgentUpsertCustomFieldValueRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        try
        {
            var result = await _customFieldService.UpsertAgentCollectedValueAsync(
                agentId,
                new AgentCustomFieldCollectedValueInput(
                    request.DefinitionId,
                    request.Name,
                    request.Value.GetRawText(),
                    request.TaskId,
                    request.ScriptId,
                    "agent"),
                cancellationToken);

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("me/nats-credentials")]
    public async Task<IActionResult> IssueNatsCredentials(CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: true);
        if (blocked is not null)
            return blocked;

        try
        {
            var credentials = await _natsCredentialsService.IssueForAgentAsync(agentId, ct);
            return Ok(credentials);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gera um token de deploy zero-touch: uso único, TTL de 1 minuto.
    /// Usado pelo agent (já autenticado) para provisionar peers sem configuração na mesma rede (discovery).
    /// Requer que DiscoveryEnabled esteja ativo na configuração efetiva do site do agent.
    /// </summary>
    [HttpPost("me/zero-touch/deploy-token")]
    public async Task<IActionResult> IssueZeroTouchDeployToken()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
        if (!resolved.DiscoveryEnabled)
            return StatusCode(403, new { error = "Zero-touch provisioning (discovery) is disabled for this site." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        var (token, rawToken) = await _deployTokenService.CreateZeroTouchTokenAsync(site.ClientId, site.Id);

        return Ok(new
        {
            token = rawToken,
            tokenId = token.Id,
            expiresAt = token.ExpiresAt,
            maxUses = token.MaxUses
        });
    }

    [HttpGet("me/hardware")]
    public async Task<IActionResult> GetHardware()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var hardware = await _hardwareRepo.GetByAgentIdAsync(agentId);
        var components = await _hardwareRepo.GetComponentsAsync(agentId);

        return Ok(new
        {
            Hardware = hardware,
            Disks = components.Disks,
            NetworkAdapters = components.NetworkAdapters,
            MemoryModules = components.MemoryModules,
            Printers = components.Printers,
            ListeningPorts = components.ListeningPorts,
            OpenSockets = components.OpenSockets
        });
    }

    [HttpPost("me/hardware")]
    public async Task<IActionResult> ReportHardwarePost([FromBody] HardwareReportRequest request)
        => await UpsertHardwareAsync(request);

    [HttpPut("me/hardware")]
    public async Task<IActionResult> ReportHardwarePut([FromBody] HardwareReportRequest request)
        => await UpsertHardwareAsync(request);

    private async Task<IActionResult> UpsertHardwareAsync(HardwareReportRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: true);
        if (blocked is not null)
            return blocked;

        var hasAgentUpdate = request.Hostname is not null
            || request.DisplayName is not null
            || request.MeshCentralNodeId is not null
            || request.Status.HasValue
            || request.OperatingSystem is not null
            || request.OsVersion is not null
            || request.AgentVersion is not null
            || request.LastIpAddress is not null
            || request.MacAddress is not null;

        if (request.MeshCentralNodeId is not null
            && !string.IsNullOrWhiteSpace(request.MeshCentralNodeId)
            && !request.MeshCentralNodeId.Trim().StartsWith("node/", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "MeshCentral node id is invalid." });
        }

        if (hasAgentUpdate)
        {
            if (request.Hostname is not null)
                agent.Hostname = request.Hostname;

            if (request.DisplayName is not null)
                agent.DisplayName = request.DisplayName;

            if (request.MeshCentralNodeId is not null)
                agent.MeshCentralNodeId = string.IsNullOrWhiteSpace(request.MeshCentralNodeId)
                    ? null
                    : request.MeshCentralNodeId.Trim();

            if (request.Status.HasValue)
                agent.Status = request.Status.Value;

            if (request.OperatingSystem is not null)
                agent.OperatingSystem = request.OperatingSystem;

            if (request.OsVersion is not null)
                agent.OsVersion = request.OsVersion;

            if (request.AgentVersion is not null)
                agent.AgentVersion = request.AgentVersion;

            if (request.LastIpAddress is not null)
                agent.LastIpAddress = request.LastIpAddress;

            if (request.MacAddress is not null)
                agent.MacAddress = request.MacAddress;

            await _agentRepo.UpdateAsync(agent);
        }

        string? inventoryRaw = null;
        if (request.InventoryRaw.HasValue && request.InventoryRaw.Value.ValueKind != JsonValueKind.Null)
            inventoryRaw = request.InventoryRaw.Value.GetRawText();

        var hasInventoryPayload = inventoryRaw is not null
            || request.InventorySchemaVersion is not null
            || request.InventoryCollectedAt.HasValue;

        if (request.Hardware is not null || hasInventoryPayload || request.Components is not null)
        {
            var hardware = request.Hardware ?? new AgentHardwareInfo { AgentId = agentId };
            hardware.AgentId = agentId;
            var reportedAt = request.InventoryCollectedAt ?? DateTime.UtcNow;

            if (hasInventoryPayload)
            {
                hardware.InventoryRaw = inventoryRaw;
                hardware.InventorySchemaVersion = request.InventorySchemaVersion;
                hardware.InventoryCollectedAt = request.InventoryCollectedAt ?? DateTime.UtcNow;
            }
            else if (request.Hardware is not null)
            {
                var existing = await _hardwareRepo.GetByAgentIdAsync(agentId);
                if (existing is not null)
                {
                    hardware.InventoryRaw = existing.InventoryRaw;
                    hardware.InventorySchemaVersion = existing.InventorySchemaVersion;
                    hardware.InventoryCollectedAt = existing.InventoryCollectedAt;
                }
            }

            var existingComponents = await _hardwareRepo.GetComponentsAsync(agentId);
            var components = request.Components;
            var componentsFromInventory = TryBuildComponentsFromInventoryRaw(inventoryRaw, agentId, reportedAt);

            var disks = components?.Disks;
            if ((disks is null || disks.Count == 0) && componentsFromInventory is not null && componentsFromInventory.Disks.Count > 0)
                disks = componentsFromInventory.Disks;
            if (disks is null || disks.Count == 0)
                disks = existingComponents.Disks;

            var networkAdapters = components?.NetworkAdapters;
            if ((networkAdapters is null || networkAdapters.Count == 0) && componentsFromInventory is not null && componentsFromInventory.NetworkAdapters.Count > 0)
                networkAdapters = componentsFromInventory.NetworkAdapters;
            if (networkAdapters is null || networkAdapters.Count == 0)
                networkAdapters = existingComponents.NetworkAdapters;

            var memoryModules = components?.MemoryModules;
            if ((memoryModules is null || memoryModules.Count == 0) && componentsFromInventory is not null && componentsFromInventory.MemoryModules.Count > 0)
                memoryModules = componentsFromInventory.MemoryModules;
            if (memoryModules is null || memoryModules.Count == 0)
                memoryModules = existingComponents.MemoryModules;

            var printers = components?.Printers;
            if ((printers is null || printers.Count == 0) && componentsFromInventory is not null && componentsFromInventory.Printers.Count > 0)
                printers = componentsFromInventory.Printers;
            if (printers is null || printers.Count == 0)
                printers = existingComponents.Printers;

            var listeningPorts = components?.ListeningPorts;
            if ((listeningPorts is null || listeningPorts.Count == 0) && componentsFromInventory is not null && componentsFromInventory.ListeningPorts.Count > 0)
                listeningPorts = componentsFromInventory.ListeningPorts;
            if (listeningPorts is null || listeningPorts.Count == 0)
                listeningPorts = existingComponents.ListeningPorts;

            var openSockets = components?.OpenSockets;
            if ((openSockets is null || openSockets.Count == 0) && componentsFromInventory is not null && componentsFromInventory.OpenSockets.Count > 0)
                openSockets = componentsFromInventory.OpenSockets;
            if (openSockets is null || openSockets.Count == 0)
                openSockets = existingComponents.OpenSockets;

            var consolidated = new AgentHardwareComponents
            {
                Disks = disks,
                NetworkAdapters = networkAdapters,
                MemoryModules = memoryModules,
                Printers = printers,
                ListeningPorts = listeningPorts,
                OpenSockets = openSockets
            };

            hardware.HardwareComponentsJson = JsonSerializer.Serialize(consolidated);
            hardware.TotalDisksCount = consolidated.Disks.Count;

            await _hardwareRepo.UpsertAsync(hardware, consolidated);
            await InvalidateAgentInventoryCachesAsync(agentId);
            await _agentAutoLabelingService.EvaluateAgentAsync(agentId, "hardware-updated");
        }

        return Ok();
    }

    private static AgentHardwareComponents? TryBuildComponentsFromInventoryRaw(string? inventoryRaw, Guid agentId, DateTime collectedAt)
    {
        if (string.IsNullOrWhiteSpace(inventoryRaw))
            return null;

        JsonElement root;
        if (!TryParseInventoryRoot(inventoryRaw, out root))
            return null;

        var result = new AgentHardwareComponents
        {
            Disks = ParseDisks(root, agentId, collectedAt),
            NetworkAdapters = ParseNetworkAdapters(root, agentId, collectedAt),
            MemoryModules = ParseMemoryModules(root, agentId, collectedAt),
            Printers = ParsePrinters(root, agentId, collectedAt),
            ListeningPorts = ParseListeningPorts(root, agentId, collectedAt),
            OpenSockets = ParseOpenSockets(root, agentId, collectedAt)
        };

        return result.Disks.Count == 0
            && result.NetworkAdapters.Count == 0
            && result.MemoryModules.Count == 0
            && result.Printers.Count == 0
            && result.ListeningPorts.Count == 0
            && result.OpenSockets.Count == 0
            ? null
            : result;
    }

    private static bool TryParseInventoryRoot(string inventoryRaw, out JsonElement root)
    {
        root = default;

        try
        {
            using var doc = JsonDocument.Parse(inventoryRaw);
            var element = doc.RootElement;

            if (element.ValueKind == JsonValueKind.String)
            {
                var innerJson = element.GetString();
                if (string.IsNullOrWhiteSpace(innerJson))
                    return false;

                using var innerDoc = JsonDocument.Parse(innerJson);
                root = innerDoc.RootElement.Clone();
                return true;
            }

            if (element.ValueKind != JsonValueKind.Object)
                return false;

            root = element.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<DiskInfo> ParseDisks(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<DiskInfo>();
        if (!root.TryGetProperty("disks", out var disksElement) || disksElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in disksElement.EnumerateArray())
        {
            var driveLetter = GetString(item, "driveLetter")?.Trim();
            if (string.IsNullOrWhiteSpace(driveLetter))
                continue;

            result.Add(new DiskInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                DriveLetter = driveLetter,
                Label = GetString(item, "label"),
                FileSystem = GetString(item, "fileSystem"),
                TotalSizeBytes = GetLong(item, "totalSizeBytes"),
                FreeSpaceBytes = GetLong(item, "freeSpaceBytes"),
                MediaType = GetString(item, "mediaType"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<NetworkAdapterInfo> ParseNetworkAdapters(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<NetworkAdapterInfo>();
        if (!root.TryGetProperty("networkAdapters", out var adaptersElement) || adaptersElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in adaptersElement.EnumerateArray())
        {
            var name = GetString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new NetworkAdapterInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Name = name,
                MacAddress = GetString(item, "macAddress"),
                IpAddress = GetString(item, "ipAddress"),
                SubnetMask = GetString(item, "subnetMask"),
                Gateway = GetString(item, "gateway"),
                DnsServers = GetString(item, "dnsServers"),
                IsDhcpEnabled = GetBool(item, "isDhcpEnabled"),
                AdapterType = GetString(item, "adapterType"),
                Speed = GetString(item, "speed"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<MemoryModuleInfo> ParseMemoryModules(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<MemoryModuleInfo>();
        if (!root.TryGetProperty("memoryModules", out var modulesElement) || modulesElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in modulesElement.EnumerateArray())
        {
            var capacityBytes = GetLong(item, "capacityBytes");
            if (capacityBytes <= 0)
                continue;

            result.Add(new MemoryModuleInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Slot = GetString(item, "slot"),
                CapacityBytes = capacityBytes,
                SpeedMhz = GetNullableInt(item, "speedMhz"),
                MemoryType = GetString(item, "memoryType"),
                Manufacturer = GetString(item, "manufacturer"),
                PartNumber = GetString(item, "partNumber"),
                SerialNumber = GetString(item, "serialNumber"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<PrinterInfo> ParsePrinters(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<PrinterInfo>();
        if (!root.TryGetProperty("printers", out var printersElement) || printersElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in printersElement.EnumerateArray())
        {
            var name = GetString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new PrinterInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Name = name,
                DriverName = GetString(item, "driverName"),
                PortName = GetString(item, "portName"),
                PrinterStatus = GetString(item, "printerStatus"),
                IsDefault = GetBool(item, "isDefault"),
                IsNetworkPrinter = GetBool(item, "isNetworkPrinter"),
                Shared = GetBool(item, "shared"),
                ShareName = GetString(item, "shareName"),
                Location = GetString(item, "location"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<ListeningPortInfo> ParseListeningPorts(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<ListeningPortInfo>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!TryGetArrayProperty(root, out var portsElement, "listeningPorts", "listening_ports"))
            return result;

        foreach (var item in portsElement.EnumerateArray())
        {
            var port = GetInt(item, "port");
            if (port <= 0)
                continue;

            var protocol = GetString(item, "protocol") ?? string.Empty;
            var address = GetString(item, "address") ?? string.Empty;
            var processId = GetInt(item, "processId", "pid");
            var dedupeKey = string.Concat(protocol, "|", address, "|", port, "|", processId);
            if (!dedupe.Add(dedupeKey))
                continue;

            result.Add(new ListeningPortInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                ProcessName = GetString(item, "processName", "name"),
                ProcessId = processId,
                ProcessPath = GetString(item, "processPath", "path"),
                Protocol = protocol,
                Address = address,
                Port = port,
                CollectedAt = collectedAt
            });

            if (result.Count >= 200)
                break;
        }

        return result;
    }

    private static List<OpenSocketInfo> ParseOpenSockets(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<OpenSocketInfo>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!TryGetArrayProperty(root, out var socketsElement, "openSockets", "open_sockets", "process_open_sockets"))
            return result;

        foreach (var item in socketsElement.EnumerateArray())
        {
            var localPort = GetInt(item, "localPort", "local_port");
            var remotePort = GetInt(item, "remotePort", "remote_port");
            if (localPort <= 0 && remotePort <= 0)
                continue;

            var protocol = GetString(item, "protocol") ?? string.Empty;
            var family = GetString(item, "family") ?? string.Empty;
            var localAddress = GetString(item, "localAddress", "local_address") ?? string.Empty;
            var remoteAddress = GetString(item, "remoteAddress", "remote_address") ?? string.Empty;
            var processId = GetInt(item, "processId", "pid");

            var dedupeKey = string.Concat(
                protocol, "|",
                family, "|",
                localAddress, "|",
                localPort, "|",
                remoteAddress, "|",
                remotePort, "|",
                processId);

            if (!dedupe.Add(dedupeKey))
                continue;

            result.Add(new OpenSocketInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                ProcessName = GetString(item, "processName", "name"),
                ProcessId = processId,
                ProcessPath = GetString(item, "processPath", "path"),
                LocalAddress = localAddress,
                LocalPort = localPort,
                RemoteAddress = remoteAddress,
                RemotePort = remotePort,
                Protocol = protocol,
                Family = family,
                CollectedAt = collectedAt
            });

            if (result.Count >= 500)
                break;
        }

        return result;
    }

    private static bool TryGetArrayProperty(JsonElement obj, out JsonElement array, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                array = value;
                return true;
            }
        }

        array = default;
        return false;
    }

    private static string? GetString(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetString(JsonElement obj, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!obj.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
                continue;

            return value.GetString();
        }

        return null;
    }

    private static long GetLong(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static int GetInt(JsonElement obj, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!obj.TryGetProperty(propertyName, out var value))
                continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.Number when value.TryGetInt32(out var number):
                    return number;
                case JsonValueKind.String when int.TryParse(value.GetString(), out var parsed):
                    return parsed;
            }
        }

        return 0;
    }

    private static int? GetNullableInt(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool GetBool(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => false
        };
    }

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
            var alertCode = GetStringProperty(root, "alert_code") ?? GetStringProperty(root, "alertCode");
            if (string.IsNullOrWhiteSpace(alertCode))
                return null;

            var severity = TryResolveMonitoringSeverity(root, out var parsedSeverity)
                ? parsedSeverity
                : request.Success
                    ? MonitoringEventSeverity.Warning
                    : MonitoringEventSeverity.Critical;

            var metricKey = GetStringProperty(root, "metric_key") ?? GetStringProperty(root, "metricKey");
            var metricValue = GetNullableDecimal(root, "metric_value") ?? GetNullableDecimal(root, "metricValue");
            var occurredAt = GetNullableDateTime(root, "occurred_at") ?? GetNullableDateTime(root, "occurredAt") ?? DateTime.UtcNow;
            var title = GetStringProperty(root, "title") ?? $"[{severity}] {alertCode}";
            var message = GetStringProperty(root, "message")
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

    private static string? GetStringProperty(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static decimal? GetNullableDecimal(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTime? GetNullableDateTime(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String when DateTime.TryParse(value.GetString(), out var parsed) => parsed.ToUniversalTime(),
            _ => null
        };
    }

    private static bool TryResolveMonitoringSeverity(JsonElement obj, out MonitoringEventSeverity severity)
    {
        var rawSeverity = GetStringProperty(obj, "severity") ?? GetStringProperty(obj, "severity_level");
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

    [HttpGet("me/commands")]
    public async Task<IActionResult> GetCommands([FromQuery] int limit = 50)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var commands = await _commandRepo.GetByAgentIdAsync(agentId, limit);
        return Ok(commands);
    }

    [HttpPost("me/automation/executions/{commandId:guid}/ack")]
    public async Task<IActionResult> AckAutomationExecution(Guid commandId, [FromBody] AutomationExecutionAckRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var command = await _commandRepo.GetByIdAsync(commandId);
        if (command is null || command.AgentId != agentId)
            return NotFound(new { error = "Command not found for this agent." });

        var correlationId = Request.Headers.TryGetValue("X-Correlation-Id", out var values)
            && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : null;

        var existing = await _automationExecutionReportRepository.GetByCommandIdAsync(commandId);
        if (existing is null)
        {
            await _automationExecutionReportRepository.CreateAsync(new AutomationExecutionReport
            {
                CommandId = commandId,
                AgentId = agentId,
                TaskId = request.TaskId,
                ScriptId = request.ScriptId,
                SourceType = request.SourceType,
                Status = AutomationExecutionStatus.Acknowledged,
                CorrelationId = correlationId,
                AckMetadataJson = request.MetadataJson,
                AcknowledgedAt = DateTime.UtcNow
            });
        }
        else
        {
            await _automationExecutionReportRepository.UpdateAckAsync(
                commandId,
                request.TaskId,
                request.ScriptId,
                request.MetadataJson,
                DateTime.UtcNow,
                correlationId);
        }

        if (command.Status == CommandStatus.Pending)
            await _commandRepo.UpdateStatusAsync(commandId, CommandStatus.Sent, command.Result, command.ExitCode, command.ErrorMessage);

        return Ok(new { acknowledged = true, commandId });
    }

    [HttpPost("me/automation/executions/{commandId:guid}/result")]
    public async Task<IActionResult> CompleteAutomationExecution(Guid commandId, [FromBody] AutomationExecutionResultRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var command = await _commandRepo.GetByIdAsync(commandId);
        if (command is null || command.AgentId != agentId)
            return NotFound(new { error = "Command not found for this agent." });

        var correlationId = Request.Headers.TryGetValue("X-Correlation-Id", out var values)
            && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : null;

        var existing = await _automationExecutionReportRepository.GetByCommandIdAsync(commandId);
        if (existing is null)
        {
            await _automationExecutionReportRepository.CreateAsync(new AutomationExecutionReport
            {
                CommandId = commandId,
                AgentId = agentId,
                TaskId = request.TaskId,
                ScriptId = request.ScriptId,
                SourceType = request.SourceType,
                Status = request.Success ? AutomationExecutionStatus.Completed : AutomationExecutionStatus.Failed,
                CorrelationId = correlationId,
                ResultMetadataJson = request.MetadataJson,
                ResultReceivedAt = DateTime.UtcNow,
                ExitCode = request.ExitCode,
                ErrorMessage = request.ErrorMessage
            });
        }
        else
        {
            await _automationExecutionReportRepository.UpdateResultAsync(
                commandId,
                request.TaskId,
                request.ScriptId,
                request.Success,
                request.ExitCode,
                request.ErrorMessage,
                request.MetadataJson,
                DateTime.UtcNow,
                correlationId);
        }

        await _commandRepo.UpdateStatusAsync(
            commandId,
            request.Success ? CommandStatus.Completed : CommandStatus.Failed,
            request.MetadataJson,
            request.ExitCode,
            request.ErrorMessage);

        try
        {
            var site = agent is null ? null : await _siteRepo.GetByIdAsync(agent.SiteId);
            if (agent is not null && site is not null)
            {
                var labels = await _agentLabelRepository.GetByAgentIdAsync(agentId);
                var report = await _automationExecutionReportRepository.GetByCommandIdAsync(commandId);
                var monitoringEvent = TryBuildMonitoringEventFromAutomationResult(
                    site.ClientId,
                    agent.SiteId,
                    agentId,
                    report?.Id,
                    correlationId,
                    request,
                    labels.Select(label => label.Label));

                if (monitoringEvent is not null)
                {
                    var createdMonitoringEvent = await _monitoringEventRepository.CreateAsync(monitoringEvent);
                    await _autoTicketOrchestratorService.EvaluateAsync(createdMonitoringEvent, HttpContext.RequestAborted);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao ingerir evento de monitoramento para o resultado da automação {CommandId} do agent {AgentId}.", commandId, agentId);
        }

        return Ok(new { completed = true, commandId, success = request.Success });
    }

    [HttpGet("me/software")]
    public async Task<IActionResult> GetSoftwareInventory()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var software = await _softwareRepo.GetCurrentByAgentIdAsync(agentId);
        return Ok(software);
    }

    [HttpPost("me/software")]
    public async Task<IActionResult> ReportSoftwarePost([FromBody] SoftwareInventoryReportRequest request)
        => await UpsertSoftwareInventoryAsync(request);

    [HttpPut("me/software")]
    public async Task<IActionResult> ReportSoftwarePut([FromBody] SoftwareInventoryReportRequest request)
        => await UpsertSoftwareInventoryAsync(request);

    private async Task<IActionResult> UpsertSoftwareInventoryAsync(SoftwareInventoryReportRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: true);
        if (blocked is not null)
            return blocked;

        var collectedAt = request.CollectedAt ?? DateTime.UtcNow;
        var software = (request.Software ?? new List<SoftwareInventoryItemRequest>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new SoftwareInventoryEntry
            {
                Name = x.Name,
                Version = x.Version,
                Publisher = x.Publisher,
                InstallId = x.InstallId,
                Serial = x.Serial,
                Source = x.Source
            });

        await _softwareRepo.ReplaceInventoryAsync(agentId, collectedAt, software);
        await InvalidateAgentInventoryCachesAsync(agentId);
        await _agentAutoLabelingService.EvaluateAgentAsync(agentId, "software-inventory-updated");
        return Ok(new { Message = "Software inventory updated." });
    }

    private async Task InvalidateAgentInventoryCachesAsync(Guid agentId)
    {
        await _redisService.DeleteAsync($"agents:hardware:{agentId:N}");
        await _redisService.DeleteAsync($"agents:software:snapshot:{agentId:N}");
        await _redisService.DeleteAsync($"agents:single:{agentId:N}");
        await _redisService.DeleteByPrefixAsync("software-inventory:");
    }

    // === TICKETS ===

    /// <summary>
    /// Retorna todos os tickets associados a este agente.
    /// </summary>
    [HttpGet("me/tickets")]
    public async Task<IActionResult> GetMyTickets([FromQuery] Guid? workflowStateId)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var tickets = await _ticketRepo.GetByAgentIdAsync(agentId, workflowStateId);
        
        // Enriquecer com informações do workflow state
        var ticketsWithState = new List<object>();
        foreach (var ticket in tickets)
        {
            var state = await _workflowRepo.GetStateByIdAsync(ticket.WorkflowStateId);
            ticketsWithState.Add(new
            {
                ticket.Id,
                ticket.ClientId,
                ticket.SiteId,
                ticket.AgentId,
                ticket.DepartmentId,
                ticket.WorkflowProfileId,
                ticket.Title,
                ticket.Description,
                ticket.Category,
                ticket.WorkflowStateId,
                WorkflowState = state != null ? new
                {
                    state.Id,
                    state.Name,
                    state.Color,
                    state.IsInitial,
                    state.IsFinal,
                    state.SortOrder
                } : null,
                ticket.Priority,
                ticket.AssignedToUserId,
                ticket.SlaExpiresAt,
                ticket.SlaBreached,
                ticket.Rating,
                ticket.RatedAt,
                ticket.RatedBy,
                ticket.CreatedAt,
                ticket.UpdatedAt,
                ticket.ClosedAt,
                ticket.DaysOpen
            });
        }
        
        return Ok(ticketsWithState);
    }

    /// <summary>
    /// Retorna um ticket específico se ele pertencer a este agente.
    /// </summary>
    [HttpGet("me/tickets/{ticketId:guid}")]
    public async Task<IActionResult> GetMyTicket(Guid ticketId)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.AgentId != agentId)
            return Forbid();

        // Enriquecer com informações do workflow state
        var state = await _workflowRepo.GetStateByIdAsync(ticket.WorkflowStateId);
        
        return Ok(new
        {
            ticket.Id,
            ticket.ClientId,
            ticket.SiteId,
            ticket.AgentId,
            ticket.DepartmentId,
            ticket.WorkflowProfileId,
            ticket.Title,
            ticket.Description,
            ticket.Category,
            ticket.WorkflowStateId,
            WorkflowState = state != null ? new
            {
                state.Id,
                state.Name,
                state.Color,
                state.IsInitial,
                state.IsFinal,
                state.SortOrder
            } : null,
            ticket.Priority,
            ticket.AssignedToUserId,
            ticket.SlaExpiresAt,
            ticket.SlaBreached,
            ticket.Rating,
            ticket.RatedAt,
            ticket.RatedBy,
            ticket.CreatedAt,
            ticket.UpdatedAt,
            ticket.ClosedAt,
            ticket.DaysOpen
        });
    }

    /// <summary>
    /// Cria um novo ticket para este agente.
    /// O agente é automaticamente associado ao ticket.
    /// </summary>
    [HttpPost("me/tickets")]
    public async Task<IActionResult> CreateMyTicket([FromBody] AgentCreateTicketRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        // Buscar o site para obter o ClientId
        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return BadRequest(new { error = "Site not found for this agent." });

        // Buscar estado inicial do workflow para o client do agente
        var initialState = await _workflowRepo.GetInitialStateAsync(site.ClientId);
        if (initialState is null)
            return BadRequest(new { error = "No initial workflow state configured for this client." });

        // Calcular SLA se houver workflow profile
        WorkflowProfile? workflowProfile = null;
        DateTime? slaExpiresAt = null;

        if (request.WorkflowProfileId.HasValue)
        {
            workflowProfile = await _workflowProfileRepo.GetByIdAsync(request.WorkflowProfileId.Value);
            if (workflowProfile is null)
                return BadRequest(new { error = "Workflow profile not found." });
        }
        else if (request.DepartmentId.HasValue)
        {
            // Pegar profile padrão do departamento se não especificado
            workflowProfile = await _workflowProfileRepo.GetDefaultByDepartmentAsync(request.DepartmentId.Value);
        }

        if (workflowProfile != null)
        {
            slaExpiresAt = await _slaService.CalculateSlaExpiryAsync(workflowProfile.Id, DateTime.UtcNow);
        }

        var effectiveWorkflowProfileId = workflowProfile?.Id;

        var ticket = new Ticket
        {
            ClientId = site.ClientId,
            SiteId = agent.SiteId,
            AgentId = agentId,
            DepartmentId = request.DepartmentId,
            WorkflowProfileId = effectiveWorkflowProfileId,
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority ?? (workflowProfile?.DefaultPriority ?? TicketPriority.Medium),
            Category = request.Category,
            WorkflowStateId = initialState.Id,
            SlaExpiresAt = slaExpiresAt
        };

        var created = await _ticketRepo.CreateAsync(ticket);

        // Log da criação
        await _activityLogService.LogActivityAsync(
            created.Id,
            TicketActivityType.Created,
            null,
            $"Agent {agent.Hostname}",
            initialState.Id.ToString(),
            "Ticket criado pelo agente"
        );

        return CreatedAtAction(nameof(GetMyTicket), new { ticketId = created.Id }, created);
    }

    /// <summary>
    /// Adiciona um comentário a um ticket do agente.
    /// </summary>
    [HttpPost("me/tickets/{ticketId:guid}/comments")]
    public async Task<IActionResult> AddMyTicketComment(Guid ticketId, [FromBody] AgentAddCommentRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.AgentId != agentId)
            return Forbid();

        var agent = await _agentRepo.GetByIdAsync(agentId);
        var comment = new TicketComment
        {
            TicketId = ticketId,
            Author = $"Agent: {agent?.Hostname ?? agentId.ToString()}",
            Content = request.Content,
            IsInternal = request.IsInternal ?? false
        };

        var created = await _ticketRepo.AddCommentAsync(comment);

        await _activityLogService.LogActivityAsync(
            ticketId,
            TicketActivityType.Commented,
            null,
            null,
            null,
            $"Comentário adicionado por {created.Author}");

        return Created($"api/agent-auth/me/tickets/{ticketId}/comments", created);
    }

    /// <summary>
    /// Lista os comentários de um ticket do agente.
    /// </summary>
    [HttpGet("me/tickets/{ticketId:guid}/comments")]
    public async Task<IActionResult> GetMyTicketComments(Guid ticketId)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.AgentId != agentId)
            return Forbid();

        var comments = await _ticketRepo.GetCommentsAsync(ticketId);
        return Ok(comments);
    }

    /// <summary>
    /// Atualiza o estado de workflow de um ticket do agente.
    /// Útil para o agente "fechar" ou "resolver" um ticket automaticamente.
    /// </summary>
    [HttpPatch("me/tickets/{ticketId:guid}/workflow-state")]
    public async Task<IActionResult> UpdateMyTicketWorkflowState(Guid ticketId, [FromBody] AgentUpdateWorkflowStateRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.AgentId != agentId)
            return Forbid();

        var agent = await _agentRepo.GetByIdAsync(agentId);

        // Validar se a transição é permitida
        var valid = await _workflowRepo.IsTransitionValidAsync(ticket.WorkflowStateId, request.WorkflowStateId, ticket.ClientId);
        if (!valid)
            return BadRequest(new { error = "Invalid workflow transition." });

        var oldStateId = ticket.WorkflowStateId;

        // Verificar se o novo estado é final (para setar ClosedAt)
        var newState = await _workflowRepo.GetStateByIdAsync(request.WorkflowStateId);
        DateTime? closedAt = newState?.IsFinal == true ? DateTime.UtcNow : null;
        ticket.ClosedAt = closedAt;

        await _ticketRepo.UpdateWorkflowStateAsync(ticketId, request.WorkflowStateId, closedAt);

        // Log da mudança de estado
        await _activityLogService.LogActivityAsync(
            ticketId,
            TicketActivityType.StateChanged,
            null,
            oldStateId.ToString(),
            request.WorkflowStateId.ToString(),
            $"Alterado pelo agente {agent?.Hostname ?? agentId.ToString()}"
        );

        // Recarregar o ticket atualizado
        var updatedTicket = await _ticketRepo.GetByIdAsync(ticketId);

        return Ok(new { message = "Workflow state updated", ticket = updatedTicket });
    }

    /// <summary>
    /// Fecha um ticket e opcionalmente avalia de 0 a 5 estrelas.
    /// Move o ticket para um estado final (Closed ou Resolved).
    /// </summary>
    [HttpPost("me/tickets/{ticketId:guid}/close")]
    public async Task<IActionResult> CloseAndRateTicket(Guid ticketId, [FromBody] AgentCloseTicketRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.AgentId != agentId)
            return Forbid();

        var agent = await _agentRepo.GetByIdAsync(agentId);

        // Validar rating se fornecido
        if (request.Rating.HasValue && (request.Rating.Value < 0 || request.Rating.Value > 5))
            return BadRequest(new { error = "Rating must be between 0 and 5." });

        // Buscar um estado final para fechar o ticket
        Guid targetStateId;
        
        if (request.WorkflowStateId.HasValue)
        {
            // Usar o estado fornecido
            var targetState = await _workflowRepo.GetStateByIdAsync(request.WorkflowStateId.Value);
            if (targetState is null)
                return BadRequest(new { error = "Workflow state not found." });
            
            if (!targetState.IsFinal)
                return BadRequest(new { error = "Specified workflow state is not a final state." });
            
            targetStateId = request.WorkflowStateId.Value;
        }
        else
        {
            // Buscar estado "Closed" ou qualquer estado final
            var finalStates = await _workflowRepo.GetStatesAsync(ticket.ClientId);
            var closedState = finalStates.FirstOrDefault(s => s.IsFinal && s.Name.Contains("Closed", StringComparison.OrdinalIgnoreCase))
                           ?? finalStates.FirstOrDefault(s => s.IsFinal);
            
            if (closedState is null)
                return BadRequest(new { error = "No final workflow state available for this client." });
            
            targetStateId = closedState.Id;
        }

        // Validar se a transição é permitida
        var valid = await _workflowRepo.IsTransitionValidAsync(ticket.WorkflowStateId, targetStateId, ticket.ClientId);
        if (!valid)
            return BadRequest(new { error = "Invalid workflow transition to close ticket." });

        var oldStateId = ticket.WorkflowStateId;

        // Atualizar o ticket
        ticket.WorkflowStateId = targetStateId;
        ticket.ClosedAt = DateTime.UtcNow;
        
        if (request.Rating.HasValue)
        {
            ticket.Rating = request.Rating.Value;
            ticket.RatedAt = DateTime.UtcNow;
            ticket.RatedBy = $"Agent: {agent?.Hostname ?? agentId.ToString()}";
        }

        await _ticketRepo.UpdateAsync(ticket);

        // Log da mudança de estado
        await _activityLogService.LogActivityAsync(
            ticketId,
            TicketActivityType.StateChanged,
            null,
            oldStateId.ToString(),
            targetStateId.ToString(),
            request.Rating.HasValue 
                ? $"Ticket fechado pelo agente {agent?.Hostname ?? agentId.ToString()} com avaliação {request.Rating.Value}/5"
                : $"Ticket fechado pelo agente {agent?.Hostname ?? agentId.ToString()}"
        );

        // Adicionar comentário se fornecido
        if (!string.IsNullOrWhiteSpace(request.Comment))
        {
            var comment = new TicketComment
            {
                TicketId = ticketId,
                Author = $"Agent: {agent?.Hostname ?? agentId.ToString()}",
                Content = request.Comment,
                IsInternal = false
            };

            var createdComment = await _ticketRepo.AddCommentAsync(comment);

            await _activityLogService.LogActivityAsync(
                ticketId,
                TicketActivityType.Commented,
                null,
                null,
                null,
                $"Comentário adicionado por {createdComment.Author}");
        }

        // Recarregar o ticket atualizado
        var updatedTicket = await _ticketRepo.GetByIdAsync(ticketId);

        return Ok(new 
        { 
            message = "Ticket closed successfully", 
            ticket = updatedTicket,
            rating = request.Rating
        });
    }

    /// <summary>
    /// Chat síncrono com IA (respostas curtas, < 5s)
    /// </summary>
    [HttpPost("me/ai-chat")]
    public async Task<IActionResult> ChatSync([FromBody] AgentChatRequest request, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        try
        {
            var response = await _aiChatService.ProcessSyncAsync(
                agentId, 
                request.Message, 
                request.SessionId, 
                ct);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(408, new { error = "Request timeout" });
        }
        catch (Exception)
        {
            return StatusCode(500, new { error = "Internal error processing chat request" });
        }
    }

    /// <summary>
    /// Chat assíncrono com IA (respostas longas, processamento em background)
    /// </summary>
    [HttpPost("me/ai-chat/async")]
    public async Task<IActionResult> ChatAsync([FromBody] AgentChatAsyncRequest request, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        try
        {
            var jobId = await _aiChatService.ProcessAsyncAsync(
                agentId, 
                request.Message, 
                request.SessionId, 
                ct);
            return Accepted(new 
            { 
                jobId, 
                statusUrl = $"/api/agent-auth/me/ai-chat/jobs/{jobId}" 
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception)
        {
            return StatusCode(500, new { error = "Internal error creating async chat job" });
        }
    }

    /// <summary>
    /// Consulta status de job assíncrono de chat
    /// </summary>
    [HttpGet("me/ai-chat/jobs/{jobId}")]
    public async Task<IActionResult> GetJobStatus(Guid jobId, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        try
        {
            var status = await _aiChatService.GetJobStatusAsync(jobId, agentId, ct);
            if (status == null)
                return NotFound(new { error = "Job not found or unauthorized" });
            
            return Ok(status);
        }
        catch (Exception)
        {
            return StatusCode(500, new { error = "Internal error retrieving job status" });
        }
    }

    /// <summary>
    /// Chat com IA via SSE streaming — tokens são entregues incrementalmente.
    /// O cliente deve consumir o response body como text/event-stream.
    ///
    /// Protocolo de eventos:
    ///   data: {"type":"token","content":"texto"}       — fragmento incremental
    ///   data: {"type":"done","sessionId":"...","latencyMs":123}  — fim do stream
    ///   data: {"type":"error","error":"mensagem"}      — erro
    /// </summary>
    [HttpPost("me/ai-chat/stream")]
    public async Task ChatStream([FromBody] AgentChatRequest request, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "Agent not authenticated." }, ct);
            return;
        }

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
        {
            HttpContext.Response.StatusCode = 423;
            await HttpContext.Response.WriteAsJsonAsync(new
            {
                error = "Agent is pending zero-touch approval.",
                zeroTouchPending = true
            }, ct);
            return;
        }

        HttpContext.Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
        HttpContext.Response.Headers["Cache-Control"] = "no-cache";
        HttpContext.Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Response.Headers["Connection"] = "keep-alive";

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        try
        {
            await foreach (var chunk in _aiChatService.StreamAsync(agentId, request.Message, request.SessionId, ct))
            {
                var data = System.Text.Json.JsonSerializer.Serialize(chunk, jsonOptions);
                await HttpContext.Response.WriteAsync($"data: {data}\n\n", ct);
                await HttpContext.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // cliente desconectou — sem ação
        }
        catch (Exception)
        {
            if (!HttpContext.Response.HasStarted) return;
            var errData = System.Text.Json.JsonSerializer.Serialize(
                new { type = "error", error = "Internal stream error" });
            await HttpContext.Response.WriteAsync($"data: {errData}\n\n", ct);
            await HttpContext.Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Registra o agent no sistema de bootstrap P2P via cloud e retorna peers online do mesmo cliente.
    /// O agent envia seu peer ID libp2p, IPs e porta; o servidor faz upsert e devolve
    /// até 3 peers aleatórios ativos (critério: Agent.LastSeenAt >= AgentOnlineGraceSeconds).
    /// Retorna 403 se CloudBootstrapEnabled estiver desabilitado para o cliente.
    /// </summary>
    [HttpPost("me/p2p/bootstrap")]
    public async Task<IActionResult> P2pBootstrap([FromBody] P2pBootstrapRequest? request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        if (request is null
            || string.IsNullOrWhiteSpace(request.PeerId)
            || string.IsNullOrWhiteSpace(request.AgentId)
            || request.Addrs is null
            || request.Port <= 0)
            return BadRequest(new { error = "Missing or invalid required fields: agentId, peerId, addrs, port." });

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);

        if (!resolved.CloudBootstrapEnabled)
            return StatusCode(403, new { error = "P2P cloud bootstrap is disabled for this client." });

        var addrsJson = System.Text.Json.JsonSerializer.Serialize(request.Addrs);
        await _p2pBootstrapRepo.UpsertAsync(new Core.Entities.AgentP2pBootstrap
        {
            AgentId = agentId,
            ClientId = resolved.ClientId ?? Guid.Empty,
            PeerId = request.PeerId,
            AddrsJson = addrsJson,
            Port = request.Port,
            LastHeartbeatAt = DateTime.UtcNow
        });

        var onlineCutoff = DateTime.UtcNow.AddSeconds(-resolved.AgentOnlineGraceSeconds);
        var peers = await _p2pBootstrapRepo.GetRandomPeersAsync(
            resolved.ClientId ?? Guid.Empty,
            agentId,
            count: 3,
            onlineCutoff);

        var peerDtos = peers.Select(p =>
        {
            string[] addrs;
            try { addrs = System.Text.Json.JsonSerializer.Deserialize<string[]>(p.AddrsJson) ?? []; }
            catch { addrs = []; }
            return new Core.DTOs.P2pBootstrapPeerDto(p.PeerId, addrs, p.Port);
        }).ToList();

        return Ok(new Core.DTOs.P2pBootstrapResponse(peerDtos));
    }

    private bool TryGetAuthenticatedAgentId(out Guid agentId)
    {
        agentId = Guid.Empty;

        if (!HttpContext.Items.TryGetValue("AgentId", out var value) || value is not Guid parsed)
            return false;

        agentId = parsed;
        return true;
    }

    private async Task<(Agent? Agent, IActionResult? Blocked)> GetAgentOrBlockPendingAsync(Guid agentId, bool allowPending)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return (null, NotFound(new { error = "Agent not found." }));

        if (agent.ZeroTouchPending && !allowPending)
        {
            return (agent, StatusCode(423, new
            {
                error = "Agent is pending zero-touch approval.",
                zeroTouchPending = true
            }));
        }

        return (agent, null);
    }

    private static string ComputeAppStoreRevision(IReadOnlyList<EffectiveApprovedAppDto> items)
    {
        if (items.Count == 0)
            return "empty";

        var builder = new StringBuilder(items.Count * 64);
        foreach (var item in items
                     .OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append(item.PackageId).Append('|')
                .Append(item.Version).Append('|')
                .Append(item.AutoUpdateEnabled).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ─── Base de Conhecimento ─────────────────────────────────────────

    /// <summary>
    /// Lista artigos da KB acessíveis pelo agente (site + cliente + global).
    /// </summary>
    [HttpGet("knowledge")]
    public async Task<IActionResult> GetKnowledge(
        [FromQuery] string? category = null,
        CancellationToken ct = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return NotFound(new { error = "Site not found." });

        var articles = await _knowledgeRepo.ListByScopeAsync(
            site.ClientId, agent.SiteId, publishedOnly: true, category, ct);

        var response = articles.Select(a => new
        {
            a.Id,
            a.Title,
            a.Category,
            Tags = string.IsNullOrEmpty(a.TagsJson)
                ? Array.Empty<string>()
                : System.Text.Json.JsonSerializer.Deserialize<string[]>(a.TagsJson),
            a.Author,
            Scope = GetKbScope(a.ClientId, a.SiteId),
            a.PublishedAt,
            a.UpdatedAt
        });

        return Ok(response);
    }

    /// <summary>
    /// Retorna o conteúdo completo de um artigo da KB (apenas se o agente tiver acesso).
    /// </summary>
    [HttpGet("knowledge/{articleId:guid}")]
    public async Task<IActionResult> GetKnowledgeArticle(
        Guid articleId,
        CancellationToken ct = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return NotFound(new { error = "Site not found." });

        var article = await _knowledgeRepo.GetByIdAsync(articleId, ct);
        if (article is null || !article.IsPublished)
            return NotFound(new { error = "Artigo não encontrado ou não publicado." });

        // Valida que o artigo está no escopo acessível pelo agente
        var accessible = article.ClientId == null   // global
            || (article.ClientId == site.ClientId && article.SiteId == null)   // cliente
            || (article.ClientId == site.ClientId && article.SiteId == agent.SiteId); // site

        if (!accessible)
            return Forbid();

        return Ok(new
        {
            article.Id,
            article.Title,
            article.Content,
            article.Category,
            Tags = string.IsNullOrEmpty(article.TagsJson)
                ? Array.Empty<string>()
                : System.Text.Json.JsonSerializer.Deserialize<string[]>(article.TagsJson),
            article.Author,
            Scope = GetKbScope(article.ClientId, article.SiteId),
            article.PublishedAt,
            article.UpdatedAt
        });
    }

    private static string GetKbScope(Guid? clientId, Guid? siteId) =>
        (clientId, siteId) switch
        {
            (null, null) => "Global",
            (not null, null) => "Client",
            _ => "Site"
        };

    private static string? BuildNatsProbeUrl(string? natsHost)
    {
        if (string.IsNullOrWhiteSpace(natsHost))
            return null;

        var cleaned = natsHost.Trim()
            .Replace("wss://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "", StringComparison.OrdinalIgnoreCase);

        var candidate = cleaned.Contains("://", StringComparison.Ordinal)
            ? cleaned
            : "https://" + cleaned;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return null;

        var builder = new UriBuilder(uri)
        {
            Scheme = "https",
            Port = uri.Port > 0 ? uri.Port : 443
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    private static string? BuildNatsCacheKey(string? natsHost)
    {
        if (string.IsNullOrWhiteSpace(natsHost))
            return null;

        var cleaned = natsHost.Trim()
            .Replace("wss://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "", StringComparison.OrdinalIgnoreCase);

        var candidate = cleaned.Contains("://", StringComparison.Ordinal)
            ? cleaned
            : "https://" + cleaned;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        var port = uri.Port > 0 ? uri.Port : 443;
        return $"AgentTlsCertHash:Nats:{host}:{port}";
    }
}

// === Agent-specific request DTOs ===

/// <summary>
/// Request para o agente criar um ticket.
/// ClientId, SiteId e AgentId são inferidos do agente autenticado.
/// </summary>
public record AgentCreateTicketRequest(
    string Title,
    string Description,
    Guid? DepartmentId = null,
    Guid? WorkflowProfileId = null,
    TicketPriority? Priority = null,
    string? Category = null);

public record AgentMeshCentralEmbedRequest(
    int? ViewMode = null,
    int? HideMask = null,
    string? MeshNodeId = null,
    string? GotoDeviceName = null);

public record AgentTlsMismatchReport(
    string Target,
    string? ObservedHash = null);

/// <summary>
/// Request para o agente adicionar um comentário a um ticket.
/// </summary>
public record AgentAddCommentRequest(
    string Content,
    bool? IsInternal = null);

/// <summary>
/// Request para o agente atualizar o estado de workflow de um ticket.
/// </summary>
public record AgentUpdateWorkflowStateRequest(
    Guid WorkflowStateId);

/// <summary>
/// Request para o agente fechar um ticket e opcionalmente avaliar (0-5 estrelas).
/// </summary>
public record AgentCloseTicketRequest(
    int? Rating = null,
    string? Comment = null,
    Guid? WorkflowStateId = null);

public record AgentUpsertCustomFieldValueRequest(
    Guid? DefinitionId,
    string? Name,
    JsonElement Value,
    Guid? TaskId = null,
    Guid? ScriptId = null);

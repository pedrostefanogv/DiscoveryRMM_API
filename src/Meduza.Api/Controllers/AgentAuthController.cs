using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

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
    private readonly IAutomationTaskService _automationTaskService;
    private readonly IAutomationExecutionReportRepository _automationExecutionReportRepository;
    private readonly ISyncPingDeliveryRepository _syncPingDeliveryRepository;
    private readonly IMeshCentralEmbeddingService _meshCentralEmbeddingService;

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
        IAutomationTaskService automationTaskService,
        IAutomationExecutionReportRepository automationExecutionReportRepository,
        ISyncPingDeliveryRepository syncPingDeliveryRepository,
        IMeshCentralEmbeddingService meshCentralEmbeddingService)
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
        _automationTaskService = automationTaskService;
        _automationExecutionReportRepository = automationExecutionReportRepository;
        _syncPingDeliveryRepository = syncPingDeliveryRepository;
        _meshCentralEmbeddingService = meshCentralEmbeddingService;
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

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return NotFound();

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
        if (resolved.AIIntegration is not null)
            resolved.AIIntegration.ApiKey = null;
        return Ok(resolved);
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

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
        if (!resolved.RemoteSupportMeshCentralEnabled)
            return StatusCode(403, new { error = "MeshCentral support is disabled for this scope." });

        var desiredViewMode = request?.ViewMode ?? 11;

        try
        {
            var embed = await _meshCentralEmbeddingService.GenerateAgentEmbedUrlAsync(
                agent,
                site.ClientId,
                desiredViewMode,
                request?.HideMask,
                request?.MeshNodeId,
                request?.GotoDeviceName,
                HttpContext.RequestAborted);

            return Ok(new
            {
                url = embed.Url,
                expiresAtUtc = embed.ExpiresAtUtc,
                viewMode = embed.ViewMode,
                hideMask = embed.HideMask,
                agentId = agent.Id,
                clientId = site.ClientId,
                siteId = site.Id
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }

    [HttpGet("me/app-store")]
    public async Task<IActionResult> GetAppStoreEffective(
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return NotFound();

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

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return NotFound();

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

    [HttpPost("me/sync/ping/{eventId:guid}/ack")]
    public async Task<IActionResult> AckSyncPing(
        Guid eventId,
        [FromBody] SyncPingAckRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

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

    [HttpGet("me/hardware")]
    public async Task<IActionResult> GetHardware()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var hardware = await _hardwareRepo.GetByAgentIdAsync(agentId);
        var components = await _hardwareRepo.GetComponentsAsync(agentId);

        return Ok(new
        {
            Hardware = hardware,
            Disks = components.Disks,
            NetworkAdapters = components.NetworkAdapters,
            MemoryModules = components.MemoryModules,
            Printers = components.Printers
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

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound();

        var hasAgentUpdate = request.Hostname is not null
            || request.DisplayName is not null
            || request.Status.HasValue
            || request.OperatingSystem is not null
            || request.OsVersion is not null
            || request.AgentVersion is not null
            || request.LastIpAddress is not null
            || request.MacAddress is not null;

        if (hasAgentUpdate)
        {
            if (request.Hostname is not null)
                agent.Hostname = request.Hostname;

            if (request.DisplayName is not null)
                agent.DisplayName = request.DisplayName;

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

            var consolidated = new AgentHardwareComponents
            {
                Disks = disks,
                NetworkAdapters = networkAdapters,
                MemoryModules = memoryModules,
                Printers = printers
            };

            hardware.HardwareComponentsJson = JsonSerializer.Serialize(consolidated);
            hardware.TotalDisksCount = consolidated.Disks.Count;

            await _hardwareRepo.UpsertAsync(hardware, consolidated);
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
            Printers = ParsePrinters(root, agentId, collectedAt)
        };

        return result.Disks.Count == 0
            && result.NetworkAdapters.Count == 0
            && result.MemoryModules.Count == 0
            && result.Printers.Count == 0
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

    private static string? GetString(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    [HttpGet("me/commands")]
    public async Task<IActionResult> GetCommands([FromQuery] int limit = 50)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var commands = await _commandRepo.GetByAgentIdAsync(agentId, limit);
        return Ok(commands);
    }

    [HttpPost("me/automation/executions/{commandId:guid}/ack")]
    public async Task<IActionResult> AckAutomationExecution(Guid commandId, [FromBody] AutomationExecutionAckRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

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

        return Ok(new { completed = true, commandId, success = request.Success });
    }

    [HttpGet("me/software")]
    public async Task<IActionResult> GetSoftwareInventory()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

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

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound();

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
        await _agentAutoLabelingService.EvaluateAgentAsync(agentId, "software-inventory-updated");
        return Ok(new { Message = "Software inventory updated." });
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

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

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

        var ticket = new Ticket
        {
            ClientId = site.ClientId,
            SiteId = agent.SiteId,
            AgentId = agentId,
            DepartmentId = request.DepartmentId,
            WorkflowProfileId = request.WorkflowProfileId,
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
        if (newState?.IsFinal == true)
            ticket.ClosedAt = DateTime.UtcNow;

        await _ticketRepo.UpdateWorkflowStateAsync(ticketId, request.WorkflowStateId);

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
            await _ticketRepo.AddCommentAsync(comment);
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

    private bool TryGetAuthenticatedAgentId(out Guid agentId)
    {
        agentId = Guid.Empty;

        if (!HttpContext.Items.TryGetValue("AgentId", out var value) || value is not Guid parsed)
            return false;

        agentId = parsed;
        return true;
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

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return NotFound(new { error = "Agent not found." });

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

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return NotFound(new { error = "Agent not found." });

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

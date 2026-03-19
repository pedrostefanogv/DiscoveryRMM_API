using System.Text.Json;
using Meduza.Api.Hubs;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AgentsController : ControllerBase
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);
    private const int AgentCacheTtlSeconds = 30;

    private enum PackageCommandOperation
    {
        Install,
        Update,
        Remove,
        UpdateOrInstall
    }

    private readonly IAgentRepository _agentRepo;
    private readonly IAgentHardwareRepository _hardwareRepo;
    private readonly IAgentSoftwareRepository _softwareRepo;
    private readonly ICommandRepository _commandRepo;
    private readonly IAgentAuthService _authService;
    private readonly IAutomationTaskService _automationTaskService;
    private readonly IAutomationScriptService _automationScriptService;
    private readonly IAutomationExecutionReportRepository _automationExecutionReportRepository;
    private readonly IAgentMessaging _messaging;
    private readonly ISiteRepository _siteRepository;
    private readonly IRedisService _redisService;
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly IConfigurationResolver _configurationResolver;

    public AgentsController(
        IAgentRepository agentRepo,
        IAgentHardwareRepository hardwareRepo,
        IAgentSoftwareRepository softwareRepo,
        ICommandRepository commandRepo,
        IAgentAuthService authService,
        IAutomationTaskService automationTaskService,
        IAutomationScriptService automationScriptService,
        IAutomationExecutionReportRepository automationExecutionReportRepository,
        IAgentMessaging messaging,
        ISiteRepository siteRepository,
        IRedisService redisService,
        IHubContext<AgentHub> hubContext,
        IConfigurationResolver configurationResolver)
    {
        _agentRepo = agentRepo;
        _hardwareRepo = hardwareRepo;
        _softwareRepo = softwareRepo;
        _commandRepo = commandRepo;
        _authService = authService;
        _automationTaskService = automationTaskService;
        _automationScriptService = automationScriptService;
        _automationExecutionReportRepository = automationExecutionReportRepository;
        _messaging = messaging;
        _siteRepository = siteRepository;
        _redisService = redisService;
        _hubContext = hubContext;
        _configurationResolver = configurationResolver;
    }

    [HttpGet("by-site/{siteId:guid}")]
    public async Task<IActionResult> GetBySite(Guid siteId)
    {
        var cacheKey = $"agents:by-site:{siteId:N}";
        var agents = await GetOrSetCacheAsync(cacheKey, async () => (await _agentRepo.GetBySiteIdAsync(siteId)).ToList()) ?? [];
        var onlineGraceSeconds = await GetOnlineGraceSecondsForSiteAsync(siteId);
        foreach (var agent in agents)
            ApplyEffectiveStatus(agent, onlineGraceSeconds);
        return Ok(agents);
    }

    [HttpGet("by-client/{clientId:guid}")]
    public async Task<IActionResult> GetByClient(Guid clientId)
    {
        var cacheKey = $"agents:by-client:{clientId:N}";
        var agents = await GetOrSetCacheAsync(cacheKey, async () => (await _agentRepo.GetByClientIdAsync(clientId)).ToList()) ?? [];
        var graceBySite = await GetOnlineGraceSecondsBySiteAsync(agents.Select(agent => agent.SiteId).Distinct());
        foreach (var agent in agents)
            ApplyEffectiveStatus(agent, graceBySite.GetValueOrDefault(agent.SiteId, 120));
        return Ok(agents);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var cacheKey = $"agents:single:{id:N}";
        var agent = await GetOrSetCacheAsync(cacheKey, () => _agentRepo.GetByIdAsync(id));
        if (agent is not null)
        {
            var onlineGraceSeconds = await GetOnlineGraceSecondsForSiteAsync(agent.SiteId);
            ApplyEffectiveStatus(agent, onlineGraceSeconds);
        }
        return agent is null ? NotFound() : Ok(agent);
    }

    private static void ApplyEffectiveStatus(Agent agent, int onlineGraceSeconds)
    {
        if (agent.Status != AgentStatus.Online)
            return;

        var cutoffUtc = DateTime.UtcNow.AddSeconds(-onlineGraceSeconds);
        if (!agent.LastSeenAt.HasValue || agent.LastSeenAt.Value < cutoffUtc)
            agent.Status = AgentStatus.Offline;
    }

    private async Task<int> GetOnlineGraceSecondsForSiteAsync(Guid siteId)
    {
        try
        {
            var resolved = await _configurationResolver.ResolveForSiteAsync(siteId);
            return resolved.AgentOnlineGraceSeconds;
        }
        catch
        {
            return 120;
        }
    }

    private async Task<Dictionary<Guid, int>> GetOnlineGraceSecondsBySiteAsync(IEnumerable<Guid> siteIds)
    {
        var ids = siteIds.Distinct().ToList();
        var tasks = ids.Select(async siteId =>
        {
            try
            {
                var resolved = await _configurationResolver.ResolveForSiteAsync(siteId);
                return (siteId, grace: resolved.AgentOnlineGraceSeconds);
            }
            catch
            {
                return (siteId, grace: 120);
            }
        });

        var entries = await Task.WhenAll(tasks);
        return entries.ToDictionary(entry => entry.siteId, entry => entry.grace);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAgentRequest request)
    {
        var agent = new Agent
        {
            SiteId = request.SiteId,
            Hostname = request.Hostname,
            DisplayName = request.DisplayName,
            OperatingSystem = request.OperatingSystem,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion
        };
        var created = await _agentRepo.CreateAsync(agent);
        await InvalidateAgentScopeCachesAsync(created.SiteId, null);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAgentRequest request)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        var previousSiteId = agent.SiteId;

        agent.SiteId = request.SiteId;
        agent.Hostname = request.Hostname;
        agent.DisplayName = request.DisplayName;

        await _agentRepo.UpdateAsync(agent);
        await InvalidateAgentScopeCachesAsync(agent.SiteId, previousSiteId);
        return Ok(agent);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        await _agentRepo.DeleteAsync(id);

        if (agent is not null)
            await InvalidateAgentScopeCachesAsync(agent.SiteId, null, id);

        return NoContent();
    }

    // --- Hardware Inventory ---

    [HttpGet("{id:guid}/hardware")]
    public async Task<IActionResult> GetHardware(Guid id)
    {
        var cacheKey = $"agents:hardware:{id:N}";
        var payload = await GetOrSetCacheAsync(cacheKey, async () =>
        {
            var hardware = await _hardwareRepo.GetByAgentIdAsync(id);
            var components = await _hardwareRepo.GetComponentsAsync(id);
            return new AgentHardwareCachePayload(
                hardware,
                components.Disks,
                components.NetworkAdapters,
                components.MemoryModules,
                components.Printers);
        }) ?? new AgentHardwareCachePayload(null, [], [], [], []);

        return Ok(new
        {
            Hardware = payload.Hardware,
            Disks = payload.Disks,
            NetworkAdapters = payload.NetworkAdapters,
            MemoryModules = payload.MemoryModules,
            Printers = payload.Printers
        });
    }

    [HttpGet("{id:guid}/software")]
    public async Task<IActionResult> GetSoftware(
        Guid id,
        [FromQuery] Guid? cursor = null,
        [FromQuery] int limit = 100,
        [FromQuery] string? search = null,
        [FromQuery] string order = "asc")
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        var normalizedOrder = order.Trim().ToLowerInvariant();
        if (normalizedOrder is not ("asc" or "desc"))
            return BadRequest(new { error = "Invalid order. Use 'asc' or 'desc'." });

        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var descending = normalizedOrder == "desc";
        var page = await _softwareRepo.GetCurrentByAgentIdPagedAsync(
            id,
            cursor,
            normalizedLimit + 1,
            normalizedSearch,
            descending);

        var hasMore = page.Count > normalizedLimit;
        var items = hasMore ? page.Take(normalizedLimit).ToList() : page.ToList();
        var nextCursor = hasMore ? items[^1].InventoryId : (Guid?)null;

        return Ok(new
        {
            items,
            count = items.Count,
            cursor,
            nextCursor,
            hasMore,
            limit = normalizedLimit,
            search = normalizedSearch,
            order = normalizedOrder
        });
    }

    [HttpGet("{id:guid}/software/snapshot")]
    public async Task<IActionResult> GetSoftwareSnapshot(Guid id)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        var cacheKey = $"agents:software:snapshot:{id:N}";
        var snapshot = await GetOrSetCacheAsync(cacheKey, async () => await _softwareRepo.GetSnapshotByAgentIdAsync(id));
        return Ok(snapshot);
    }

    private async Task<T?> GetOrSetCacheAsync<T>(string cacheKey, Func<Task<T?>> factory)
    {
        var cached = await _redisService.GetAsync(cacheKey);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            try
            {
                var deserialized = JsonSerializer.Deserialize<T>(cached, CacheJsonOptions);
                if (deserialized is not null)
                    return deserialized;
            }
            catch (JsonException)
            {
                await _redisService.DeleteAsync(cacheKey);
            }
        }

        var value = await factory();
        if (value is null)
            return value;

        var payload = JsonSerializer.Serialize(value, CacheJsonOptions);
        await _redisService.SetAsync(cacheKey, payload, AgentCacheTtlSeconds);
        return value;
    }

    // --- Commands ---

    [HttpGet("{id:guid}/commands")]
    public async Task<IActionResult> GetCommands(Guid id, [FromQuery] int limit = 50)
    {
        var commands = await _commandRepo.GetByAgentIdAsync(id, limit);
        return Ok(commands);
    }

    [HttpPost("{id:guid}/commands")]
    public async Task<IActionResult> SendCommand(Guid id, [FromBody] SendCommandRequest request)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        var command = new AgentCommand
        {
            AgentId = id,
            CommandType = request.CommandType,
            Payload = request.Payload
        };
        var created = await DispatchCommandAsync(command);

        return CreatedAtAction(nameof(GetCommands), new { id }, created);
    }

    [HttpPost("{id:guid}/automation/tasks/{taskId:guid}/run-now")]
    public async Task<IActionResult> RunAutomationTaskNow(Guid id, Guid taskId, CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        var task = await _automationTaskService.GetByIdAsync(taskId, includeInactive: false, cancellationToken);
        if (task is null)
            return NotFound(new { error = "Automation task not found or inactive." });

        var command = await BuildAgentCommandFromTaskAsync(id, task, cancellationToken);
        var created = await DispatchCommandAsync(command);
        await CreateExecutionReportAsync(created, task.Id, task.ScriptId, AutomationExecutionSourceType.RunNow, new
        {
            mode = "task-run-now",
            actionType = task.ActionType.ToString()
        });

        return CreatedAtAction(nameof(GetCommands), new { id }, new
        {
            command = created,
            automationTaskId = task.Id,
            actionType = task.ActionType.ToString()
        });
    }

    [HttpPost("{id:guid}/automation/scripts/{scriptId:guid}/run-now")]
    public async Task<IActionResult> RunAutomationScriptNow(Guid id, Guid scriptId, CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        var script = await _automationScriptService.GetByIdAsync(scriptId, includeInactive: false, cancellationToken);
        if (script is null)
            return NotFound(new { error = "Automation script not found or inactive." });

        var command = new AgentCommand
        {
            AgentId = id,
            CommandType = CommandType.Script,
            Payload = script.Content
        };

        var created = await DispatchCommandAsync(command);
        await CreateExecutionReportAsync(created, null, script.Id, AutomationExecutionSourceType.RunNow, new
        {
            mode = "script-run-now",
            version = script.Version,
            contentHash = script.ContentHashSha256
        });
        return CreatedAtAction(nameof(GetCommands), new { id }, new
        {
            command = created,
            automationScriptId = script.Id,
            scriptVersion = script.Version,
            contentHash = script.ContentHashSha256
        });
    }

    [HttpPost("{id:guid}/automation/force-sync")]
    public async Task<IActionResult> ForceAutomationSync(Guid id, [FromBody] ForceAutomationSyncRequest? request)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        var normalized = request ?? new ForceAutomationSyncRequest();
        var payload = JsonSerializer.Serialize(new
        {
            Operation = "force-sync",
            Policies = normalized.Policies,
            Inventory = normalized.Inventory,
            Software = normalized.Software,
            AppStore = normalized.AppStore,
            RequestedAt = DateTime.UtcNow
        });

        var command = new AgentCommand
        {
            AgentId = id,
            // SystemInfo permite reaproveitar pipeline de sincronizacao de estado do agente.
            CommandType = CommandType.SystemInfo,
            Payload = payload
        };

        var created = await DispatchCommandAsync(command);
        await CreateExecutionReportAsync(created, null, null, AutomationExecutionSourceType.ForceSync, normalized);
        return CreatedAtAction(nameof(GetCommands), new { id }, new
        {
            command = created,
            sync = normalized
        });
    }

    private async Task InvalidateAgentScopeCachesAsync(Guid currentSiteId, Guid? previousSiteId = null, Guid? agentId = null)
    {
        await _redisService.DeleteAsync("agents:all-ids");
        await _redisService.DeleteByPrefixAsync("software-inventory:");

        var siteIds = new HashSet<Guid> { currentSiteId };
        if (previousSiteId.HasValue)
            siteIds.Add(previousSiteId.Value);

        foreach (var siteId in siteIds)
        {
            await _redisService.DeleteAsync($"agents:by-site:{siteId:N}");

            var site = await _siteRepository.GetByIdAsync(siteId);
            if (site is not null)
                await _redisService.DeleteAsync($"agents:by-client:{site.ClientId:N}");
        }

        if (agentId.HasValue)
        {
            await _redisService.DeleteAsync($"agents:single:{agentId.Value:N}");
            await _redisService.DeleteAsync($"agents:hardware:{agentId.Value:N}");
            await _redisService.DeleteAsync($"agents:software:snapshot:{agentId.Value:N}");
        }
    }

    private sealed record AgentHardwareCachePayload(
        AgentHardwareInfo? Hardware,
        IReadOnlyList<DiskInfo> Disks,
        IReadOnlyList<NetworkAdapterInfo> NetworkAdapters,
        IReadOnlyList<MemoryModuleInfo> MemoryModules,
        IReadOnlyList<PrinterInfo> Printers);

    private async Task<AgentCommand> BuildAgentCommandFromTaskAsync(Guid agentId, AutomationTaskDetailDto task, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        return task.ActionType switch
        {
            AutomationTaskActionType.RunScript => await BuildRunScriptCommandAsync(agentId, task),
            AutomationTaskActionType.InstallPackage => BuildPackageCommand(agentId, task, PackageCommandOperation.Install),
            AutomationTaskActionType.UpdatePackage => BuildPackageCommand(agentId, task, PackageCommandOperation.Update),
            AutomationTaskActionType.RemovePackage => BuildPackageCommand(agentId, task, PackageCommandOperation.Remove),
            AutomationTaskActionType.UpdateOrInstallPackage => BuildPackageCommand(agentId, task, PackageCommandOperation.UpdateOrInstall),
            AutomationTaskActionType.CustomCommand => BuildCustomCommand(agentId, task),
            _ => throw new InvalidOperationException("Unsupported automation task action type.")
        };
    }

    private async Task<AgentCommand> BuildRunScriptCommandAsync(Guid agentId, AutomationTaskDetailDto task)
    {
        if (!task.ScriptId.HasValue)
            throw new InvalidOperationException("Automation task has no ScriptId for RunScript action.");

        var script = await _automationScriptService.GetByIdAsync(task.ScriptId.Value, includeInactive: false);
        if (script is null)
            throw new InvalidOperationException("Referenced automation script not found or inactive.");

        return new AgentCommand
        {
            AgentId = agentId,
            CommandType = CommandType.Script,
            Payload = script.Content
        };
    }

    private static AgentCommand BuildPackageCommand(Guid agentId, AutomationTaskDetailDto task, PackageCommandOperation operation)
    {
        if (!task.InstallationType.HasValue || string.IsNullOrWhiteSpace(task.PackageId))
            throw new InvalidOperationException("Automation task package action requires InstallationType and PackageId.");

        var packageId = task.PackageId.Trim();
        var payload = task.InstallationType.Value switch
        {
            AppInstallationType.Winget => operation switch
            {
                PackageCommandOperation.Install
                    => $"winget install --id {packageId} --silent --accept-package-agreements --accept-source-agreements",
                PackageCommandOperation.Update
                    => $"winget upgrade --id {packageId} --silent --accept-package-agreements --accept-source-agreements",
                PackageCommandOperation.Remove
                    => $"winget uninstall --id {packageId} --silent --accept-source-agreements",
                PackageCommandOperation.UpdateOrInstall
                    => $"winget upgrade --id {packageId} --silent --accept-package-agreements --accept-source-agreements ; if ($LASTEXITCODE -ne 0) {{ winget install --id {packageId} --silent --accept-package-agreements --accept-source-agreements }}",
                _ => throw new InvalidOperationException("Unsupported package command operation for run-now command.")
            },
            AppInstallationType.Chocolatey => operation switch
            {
                PackageCommandOperation.Install => $"choco install {packageId} -y",
                PackageCommandOperation.Update => $"choco upgrade {packageId} -y",
                PackageCommandOperation.Remove => $"choco uninstall {packageId} -y",
                PackageCommandOperation.UpdateOrInstall => $"choco upgrade {packageId} -y --ignore-not-installed ; if ($LASTEXITCODE -ne 0) {{ choco install {packageId} -y }}",
                _ => throw new InvalidOperationException("Unsupported package command operation for run-now command.")
            },
            _ => throw new InvalidOperationException("Unsupported package installation type for run-now command.")
        };

        return new AgentCommand
        {
            AgentId = agentId,
            CommandType = CommandType.PowerShell,
            Payload = payload
        };
    }

    private static AgentCommand BuildCustomCommand(Guid agentId, AutomationTaskDetailDto task)
    {
        if (string.IsNullOrWhiteSpace(task.CommandPayload))
            throw new InvalidOperationException("Automation task custom action requires CommandPayload.");

        return new AgentCommand
        {
            AgentId = agentId,
            CommandType = CommandType.PowerShell,
            Payload = task.CommandPayload
        };
    }

    private async Task<AgentCommand> DispatchCommandAsync(AgentCommand command)
    {
        var created = await _commandRepo.CreateAsync(command);

        var sent = false;

        if (_messaging.IsConnected)
        {
            try
            {
                await _messaging.SendCommandAsync(created.AgentId, created.Id, created.CommandType.ToString(), created.Payload);
                sent = true;
            }
            catch
            {
                sent = false;
            }
        }

        if (!sent && AgentHub.IsAgentConnected(created.AgentId))
        {
            await _hubContext.Clients.Group($"agent-{created.AgentId}")
                .SendAsync("ExecuteCommand", created.Id, created.CommandType, created.Payload);
            sent = true;
        }

        if (sent)
            await _commandRepo.UpdateStatusAsync(created.Id, CommandStatus.Sent, null, null, null);

        return created;
    }

    [HttpGet("{id:guid}/automation/executions")]
    public async Task<IActionResult> GetAutomationExecutionHistory(Guid id, [FromQuery] int limit = 50)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        var items = await _automationExecutionReportRepository.GetByAgentIdAsync(id, limit);
        return Ok(items.Select(item => new AutomationExecutionReportDto
        {
            Id = item.Id,
            CommandId = item.CommandId,
            AgentId = item.AgentId,
            TaskId = item.TaskId,
            ScriptId = item.ScriptId,
            SourceType = item.SourceType.ToString(),
            Status = item.Status.ToString(),
            CorrelationId = item.CorrelationId,
            CreatedAt = item.CreatedAt,
            AcknowledgedAt = item.AcknowledgedAt,
            ResultReceivedAt = item.ResultReceivedAt,
            ExitCode = item.ExitCode,
            ErrorMessage = item.ErrorMessage,
            RequestMetadataJson = item.RequestMetadataJson,
            AckMetadataJson = item.AckMetadataJson,
            ResultMetadataJson = item.ResultMetadataJson
        }));
    }

    private async Task CreateExecutionReportAsync(AgentCommand command, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, object requestMetadata)
    {
        await _automationExecutionReportRepository.CreateAsync(new AutomationExecutionReport
        {
            CommandId = command.Id,
            AgentId = command.AgentId,
            TaskId = taskId,
            ScriptId = scriptId,
            SourceType = sourceType,
            Status = AutomationExecutionStatus.Dispatched,
            RequestMetadataJson = JsonSerializer.Serialize(requestMetadata)
        });
    }

    // --- Token Management ---

    [HttpGet("{id:guid}/tokens")]
    public async Task<IActionResult> GetTokens(Guid id)
    {
        var tokens = await _authService.GetTokensByAgentIdAsync(id);
        return Ok(tokens);
    }

    [HttpPost("{id:guid}/tokens")]
    public async Task<IActionResult> CreateToken(Guid id, [FromBody] CreateTokenRequest request)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        var (token, rawToken) = await _authService.CreateTokenAsync(id, request.Description);
        return Ok(new { Token = rawToken, Id = token.Id, ExpiresAt = token.ExpiresAt });
    }

    [HttpDelete("{id:guid}/tokens/{tokenId:guid}")]
    public async Task<IActionResult> RevokeToken(Guid id, Guid tokenId)
    {
        await _authService.RevokeTokenAsync(tokenId);
        return NoContent();
    }

    [HttpDelete("{id:guid}/tokens")]
    public async Task<IActionResult> RevokeAllTokens(Guid id)
    {
        await _authService.RevokeAllTokensAsync(id);
        return NoContent();
    }
}

public record CreateAgentRequest(Guid SiteId, string Hostname, string? DisplayName, string? OperatingSystem, string? OsVersion, string? AgentVersion);
public record UpdateAgentRequest(Guid SiteId, string Hostname, string? DisplayName);
public record SendCommandRequest(CommandType CommandType, string Payload);
public record HardwareReportRequest(
    string? Hostname,
    string? DisplayName,
    AgentStatus? Status,
    string? OperatingSystem,
    string? OsVersion,
    string? AgentVersion,
    string? LastIpAddress,
    string? MacAddress,
    AgentHardwareInfo? Hardware,
    HardwareComponentsPayload? Components,
    JsonElement? InventoryRaw,
    string? InventorySchemaVersion,
    DateTime? InventoryCollectedAt);
public record HardwareComponentsPayload(
    List<DiskInfo>? Disks,
    List<NetworkAdapterInfo>? NetworkAdapters,
    List<MemoryModuleInfo>? MemoryModules,
    List<PrinterInfo>? Printers);
public record CreateTokenRequest(string? Description);
public record ForceAutomationSyncRequest(
    bool Policies = true,
    bool Inventory = false,
    bool Software = false,
    bool AppStore = false);
public record SoftwareInventoryReportRequest(
    DateTime? CollectedAt,
    List<SoftwareInventoryItemRequest>? Software);

public record SoftwareInventoryItemRequest(
    string Name,
    string? Version,
    string? Publisher,
    string? InstallId,
    string? Serial,
    string? Source);

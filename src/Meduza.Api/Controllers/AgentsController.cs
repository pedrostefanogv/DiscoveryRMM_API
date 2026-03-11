using System.Text.Json;
using Meduza.Api.Hubs;
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
    private readonly IAgentRepository _agentRepo;
    private readonly IAgentHardwareRepository _hardwareRepo;
    private readonly IAgentSoftwareRepository _softwareRepo;
    private readonly ICommandRepository _commandRepo;
    private readonly IAgentAuthService _authService;
    private readonly IAgentMessaging _messaging;
    private readonly IHubContext<AgentHub> _hubContext;
    private readonly int _agentOnlineGraceSeconds;

    public AgentsController(
        IAgentRepository agentRepo,
        IAgentHardwareRepository hardwareRepo,
        IAgentSoftwareRepository softwareRepo,
        ICommandRepository commandRepo,
        IAgentAuthService authService,
        IAgentMessaging messaging,
        IHubContext<AgentHub> hubContext,
        IConfiguration configuration)
    {
        _agentRepo = agentRepo;
        _hardwareRepo = hardwareRepo;
        _softwareRepo = softwareRepo;
        _commandRepo = commandRepo;
        _authService = authService;
        _messaging = messaging;
        _hubContext = hubContext;
        _agentOnlineGraceSeconds = configuration.GetValue<int?>("Realtime:AgentOnlineGraceSeconds") ?? 120;
    }

    [HttpGet("by-site/{siteId:guid}")]
    public async Task<IActionResult> GetBySite(Guid siteId)
    {
        var agents = (await _agentRepo.GetBySiteIdAsync(siteId)).ToList();
        foreach (var agent in agents)
            ApplyEffectiveStatus(agent);
        return Ok(agents);
    }

    [HttpGet("by-client/{clientId:guid}")]
    public async Task<IActionResult> GetByClient(Guid clientId)
    {
        var agents = (await _agentRepo.GetByClientIdAsync(clientId)).ToList();
        foreach (var agent in agents)
            ApplyEffectiveStatus(agent);
        return Ok(agents);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is not null)
            ApplyEffectiveStatus(agent);
        return agent is null ? NotFound() : Ok(agent);
    }

    private void ApplyEffectiveStatus(Agent agent)
    {
        if (agent.Status != AgentStatus.Online)
            return;

        var cutoffUtc = DateTime.UtcNow.AddSeconds(-_agentOnlineGraceSeconds);
        if (!agent.LastSeenAt.HasValue || agent.LastSeenAt.Value < cutoffUtc)
            agent.Status = AgentStatus.Offline;
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
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAgentRequest request)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        agent.SiteId = request.SiteId;
        agent.Hostname = request.Hostname;
        agent.DisplayName = request.DisplayName;

        await _agentRepo.UpdateAsync(agent);
        return Ok(agent);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await _agentRepo.DeleteAsync(id);
        return NoContent();
    }

    // --- Hardware Inventory ---

    [HttpGet("{id:guid}/hardware")]
    public async Task<IActionResult> GetHardware(Guid id)
    {
        var hardware = await _hardwareRepo.GetByAgentIdAsync(id);
        var components = await _hardwareRepo.GetComponentsAsync(id);
        return Ok(new
        {
            Hardware = hardware,
            Disks = components.Disks,
            NetworkAdapters = components.NetworkAdapters,
            MemoryModules = components.MemoryModules
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

        var snapshot = await _softwareRepo.GetSnapshotByAgentIdAsync(id);
        return Ok(snapshot);
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
        var created = await _commandRepo.CreateAsync(command);

        var sent = false;

        // Tenta NATS primeiro; se falhar, cai para SignalR.
        if (_messaging.IsConnected)
        {
            try
            {
                await _messaging.SendCommandAsync(id, created.Id, created.CommandType.ToString(), created.Payload);
                sent = true;
            }
            catch
            {
                sent = false;
            }
        }

        if (!sent && AgentHub.IsAgentConnected(id))
        {
            await _hubContext.Clients.Group($"agent-{id}")
                .SendAsync("ExecuteCommand", created.Id, created.CommandType, created.Payload);
            sent = true;
        }

        if (sent)
            await _commandRepo.UpdateStatusAsync(created.Id, CommandStatus.Sent, null, null, null);

        return CreatedAtAction(nameof(GetCommands), new { id }, created);
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

        var (token, rawToken) = await _authService.CreateTokenAsync(id, request.Description, request.ExpirationDays);
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
    List<MemoryModuleInfo>? MemoryModules);
public record CreateTokenRequest(string? Description, int? ExpirationDays);
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

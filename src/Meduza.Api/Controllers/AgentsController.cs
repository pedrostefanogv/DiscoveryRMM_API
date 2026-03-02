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
    private readonly ICommandRepository _commandRepo;
    private readonly IAgentAuthService _authService;
    private readonly IAgentMessaging _messaging;
    private readonly IHubContext<AgentHub> _hubContext;

    public AgentsController(
        IAgentRepository agentRepo,
        IAgentHardwareRepository hardwareRepo,
        ICommandRepository commandRepo,
        IAgentAuthService authService,
        IAgentMessaging messaging,
        IHubContext<AgentHub> hubContext)
    {
        _agentRepo = agentRepo;
        _hardwareRepo = hardwareRepo;
        _commandRepo = commandRepo;
        _authService = authService;
        _messaging = messaging;
        _hubContext = hubContext;
    }

    [HttpGet("by-site/{siteId:guid}")]
    public async Task<IActionResult> GetBySite(Guid siteId)
    {
        var agents = await _agentRepo.GetBySiteIdAsync(siteId);
        return Ok(agents);
    }

    [HttpGet("by-client/{clientId:guid}")]
    public async Task<IActionResult> GetByClient(Guid clientId)
    {
        var agents = await _agentRepo.GetByClientIdAsync(clientId);
        return Ok(agents);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        return agent is null ? NotFound() : Ok(agent);
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
        var disks = await _hardwareRepo.GetDisksAsync(id);
        var network = await _hardwareRepo.GetNetworkAdaptersAsync(id);
        var memory = await _hardwareRepo.GetMemoryModulesAsync(id);
        return Ok(new { Hardware = hardware, Disks = disks, NetworkAdapters = network, MemoryModules = memory });
    }

    [HttpPost("{id:guid}/hardware")]
    public async Task<IActionResult> ReportHardware(Guid id, [FromBody] HardwareReportRequest request)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();

        string? inventoryRaw = null;
        if (request.InventoryRaw.HasValue && request.InventoryRaw.Value.ValueKind != JsonValueKind.Null)
            inventoryRaw = request.InventoryRaw.Value.GetRawText();

        var hasInventoryPayload = inventoryRaw is not null
            || request.InventorySchemaVersion is not null
            || request.InventoryCollectedAt.HasValue;

        if (request.Hardware is not null || hasInventoryPayload)
        {
            var hardware = request.Hardware ?? new AgentHardwareInfo { AgentId = id };
            hardware.AgentId = id;
            if (hasInventoryPayload)
            {
                hardware.InventoryRaw = inventoryRaw;
                hardware.InventorySchemaVersion = request.InventorySchemaVersion;
                hardware.InventoryCollectedAt = request.InventoryCollectedAt ?? DateTime.UtcNow;
            }
            else if (request.Hardware is not null)
            {
                var existing = await _hardwareRepo.GetByAgentIdAsync(id);
                if (existing is not null)
                {
                    hardware.InventoryRaw = existing.InventoryRaw;
                    hardware.InventorySchemaVersion = existing.InventorySchemaVersion;
                    hardware.InventoryCollectedAt = existing.InventoryCollectedAt;
                }
            }
            await _hardwareRepo.UpsertAsync(hardware);
        }
        if (request.Disks is not null)
            await _hardwareRepo.ReplaceDiskInfoAsync(id, request.Disks);
        if (request.NetworkAdapters is not null)
            await _hardwareRepo.ReplaceNetworkAdaptersAsync(id, request.NetworkAdapters);
        if (request.MemoryModules is not null)
            await _hardwareRepo.ReplaceMemoryModulesAsync(id, request.MemoryModules);

        return Ok();
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

        // Tentar enviar via NATS primeiro; fallback para SignalR
        if (_messaging.IsConnected)
        {
            await _messaging.SendCommandAsync(id, created.Id, created.CommandType.ToString(), created.Payload);
            await _commandRepo.UpdateStatusAsync(created.Id, CommandStatus.Sent, null, null, null);
        }
        else if (AgentHub.IsAgentConnected(id))
        {
            await _hubContext.Clients.Group($"agent-{id}")
                .SendAsync("ExecuteCommand", created.Id, created.CommandType, created.Payload);
            await _commandRepo.UpdateStatusAsync(created.Id, CommandStatus.Sent, null, null, null);
        }

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
    AgentHardwareInfo? Hardware,
    List<DiskInfo>? Disks,
    List<NetworkAdapterInfo>? NetworkAdapters,
    List<MemoryModuleInfo>? MemoryModules,
    JsonElement? InventoryRaw,
    string? InventorySchemaVersion,
    DateTime? InventoryCollectedAt);
public record CreateTokenRequest(string? Description, int? ExpirationDays);

using System.Text.Json;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/agent-auth")]
public class AgentAuthController : ControllerBase
{
    private readonly IAgentRepository _agentRepo;
    private readonly IAgentHardwareRepository _hardwareRepo;
    private readonly IAgentSoftwareRepository _softwareRepo;
    private readonly ICommandRepository _commandRepo;

    public AgentAuthController(
        IAgentRepository agentRepo,
        IAgentHardwareRepository hardwareRepo,
        IAgentSoftwareRepository softwareRepo,
        ICommandRepository commandRepo)
    {
        _agentRepo = agentRepo;
        _hardwareRepo = hardwareRepo;
        _softwareRepo = softwareRepo;
        _commandRepo = commandRepo;
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        return agent is null ? NotFound() : Ok(agent);
    }

    [HttpGet("me/hardware")]
    public async Task<IActionResult> GetHardware()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var hardware = await _hardwareRepo.GetByAgentIdAsync(agentId);
        var disks = await _hardwareRepo.GetDisksAsync(agentId);
        var network = await _hardwareRepo.GetNetworkAdaptersAsync(agentId);
        var memory = await _hardwareRepo.GetMemoryModulesAsync(agentId);

        return Ok(new { Hardware = hardware, Disks = disks, NetworkAdapters = network, MemoryModules = memory });
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

        if (request.Hardware is not null || hasInventoryPayload)
        {
            var hardware = request.Hardware ?? new AgentHardwareInfo { AgentId = agentId };
            hardware.AgentId = agentId;

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

            await _hardwareRepo.UpsertAsync(hardware);
        }

        if (request.Disks is not null)
            await _hardwareRepo.ReplaceDiskInfoAsync(agentId, request.Disks);

        if (request.NetworkAdapters is not null)
            await _hardwareRepo.ReplaceNetworkAdaptersAsync(agentId, request.NetworkAdapters);

        if (request.MemoryModules is not null)
            await _hardwareRepo.ReplaceMemoryModulesAsync(agentId, request.MemoryModules);

        return Ok();
    }

    [HttpGet("me/commands")]
    public async Task<IActionResult> GetCommands([FromQuery] int limit = 50)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var commands = await _commandRepo.GetByAgentIdAsync(agentId, limit);
        return Ok(commands);
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
        return Ok(new { Message = "Software inventory updated." });
    }

    private bool TryGetAuthenticatedAgentId(out Guid agentId)
    {
        agentId = Guid.Empty;

        if (!HttpContext.Items.TryGetValue("AgentId", out var value) || value is not Guid parsed)
            return false;

        agentId = parsed;
        return true;
    }
}

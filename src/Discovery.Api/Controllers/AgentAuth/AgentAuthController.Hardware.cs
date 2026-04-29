using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent hardware inventory reporting endpoints.
/// </summary>
public partial class AgentAuthController
{
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
            if (request.Hostname is not null) agent!.Hostname = request.Hostname;
            if (request.DisplayName is not null) agent!.DisplayName = request.DisplayName;
            if (request.MeshCentralNodeId is not null) agent!.MeshCentralNodeId = string.IsNullOrWhiteSpace(request.MeshCentralNodeId) ? null : request.MeshCentralNodeId.Trim();
            if (request.Status.HasValue) agent!.Status = request.Status.Value;
            if (request.OperatingSystem is not null) agent!.OperatingSystem = request.OperatingSystem;
            if (request.OsVersion is not null) agent!.OsVersion = request.OsVersion;
            if (request.AgentVersion is not null) agent!.AgentVersion = request.AgentVersion;
            if (request.LastIpAddress is not null) agent!.LastIpAddress = request.LastIpAddress;
            if (request.MacAddress is not null) agent!.MacAddress = request.MacAddress;

            await _agentRepo.UpdateAsync(agent!);
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
            var componentsFromInventory = HardwareInventoryParser.TryBuildFromInventoryRaw(inventoryRaw, agentId, reportedAt);

            var disks = components?.Disks ?? componentsFromInventory?.Disks ?? existingComponents.Disks;
            var networkAdapters = components?.NetworkAdapters ?? componentsFromInventory?.NetworkAdapters ?? existingComponents.NetworkAdapters;
            var memoryModules = components?.MemoryModules ?? componentsFromInventory?.MemoryModules ?? existingComponents.MemoryModules;
            var printers = components?.Printers ?? componentsFromInventory?.Printers ?? existingComponents.Printers;
            var listeningPorts = components?.ListeningPorts ?? componentsFromInventory?.ListeningPorts ?? existingComponents.ListeningPorts;
            var openSockets = components?.OpenSockets ?? componentsFromInventory?.OpenSockets ?? existingComponents.OpenSockets;

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
}

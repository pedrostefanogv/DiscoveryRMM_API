using Discovery.Core.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent software inventory reporting endpoints.
/// </summary>
public partial class AgentAuthController
{
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

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: true);
        if (blocked is not null)
            return blocked;

        var collectedAt = request.CollectedAt ?? DateTime.UtcNow;
        var software = (request.Software ?? [])
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
}

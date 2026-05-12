using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

public partial class AgentsController
{
    [HttpPatch("{id:guid}/maintenance")]
    public async Task<IActionResult> SetMaintenance(Guid id, [FromBody] SetAgentMaintenanceRequest request)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        var site = await _siteRepository.GetByIdAsync(agent.SiteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        if (!await _permissionService.HasPermissionAsync(userId, ResourceType.Agents, ActionType.Edit, ScopeLevel.Site, agent.SiteId, site.ClientId))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Access denied. Requires Agents.Edit permission at the agent's site scope." });

        await _agentRepo.SetMaintenanceAsync(id, request.Enabled, request.Reason, userId);

        var now = DateTime.UtcNow;
        var effectiveStatus = request.Enabled ? "Maintenance" : await RecalculateEffectiveStatusAsync(id);

        await _messaging.PublishDashboardEventAsync(
            DashboardEventMessage.Create("AgentStatusChanged", new
            {
                agentId = id,
                maintenanceEnabled = request.Enabled,
                effectiveStatus,
                changedAtUtc = now
            }, site.ClientId, agent.SiteId));

        return Ok(new SetAgentMaintenanceResponse(
            AgentId: id,
            MaintenanceEnabled: request.Enabled,
            EffectiveStatus: effectiveStatus,
            ChangedAtUtc: now,
            Reason: request.Reason));
    }

    private async Task<string> RecalculateEffectiveStatusAsync(Guid agentId)
    {
        var heartbeat = await _heartbeatCache.GetHeartbeatAsync(agentId);
        if (heartbeat is not null)
            return "Online";
        return "Offline";
    }
}

using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent CRUD, zero-touch approval, custom fields, and list-by-scope endpoints.
/// </summary>
public partial class AgentsController
{
    [HttpPost("{agentId:guid}/approve-zero-touch")]
    public async Task<IActionResult> ApproveZeroTouch(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return NotFound(new { error = "Agent not found." });
        if (!agent.ZeroTouchPending) return Ok(new { message = "Agent is not pending zero-touch approval.", agentId });

        await _agentRepo.ApproveZeroTouchAsync(agentId);
        var ping = new SyncInvalidationPingDto
        {
            EventId = Guid.NewGuid(), AgentId = agentId, Resource = SyncResourceType.ZeroTouchApproved,
            ScopeType = AppApprovalScopeType.Agent, ScopeId = agentId,
            Revision = $"zero-touch:{DateTime.UtcNow:O}", Reason = "zero-touch-approved", ChangedAtUtc = DateTime.UtcNow
        };
        await _messaging.PublishSyncPingAsync(agentId, SyncInvalidationPingMessage.FromDto(ping), cancellationToken);
        return Ok(new { message = "Agent approved.", agentId });
    }

    [HttpGet("by-site/{siteId:guid}")]
    public async Task<IActionResult> GetBySite(Guid siteId)
    {
        var cacheKey = $"agents:by-site:{siteId:N}";
        var agents = await GetOrSetCacheAsync(cacheKey, async () => (await _agentRepo.GetBySiteIdAsync(siteId)).ToList()) ?? [];
        var onlineGraceSeconds = await GetOnlineGraceSecondsForSiteAsync(siteId);
        foreach (var agent in agents) ApplyEffectiveStatus(agent, onlineGraceSeconds);
        return Ok(agents);
    }

    [HttpGet("by-client/{clientId:guid}")]
    public async Task<IActionResult> GetByClient(Guid clientId)
    {
        var cacheKey = $"agents:by-client:{clientId:N}";
        var agents = await GetOrSetCacheAsync(cacheKey, async () => (await _agentRepo.GetByClientIdAsync(clientId)).ToList()) ?? [];
        var graceBySite = await GetOnlineGraceSecondsBySiteAsync(agents.Select(a => a.SiteId).Distinct());
        foreach (var agent in agents) ApplyEffectiveStatus(agent, graceBySite.GetValueOrDefault(agent.SiteId, 120));
        return Ok(agents);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var cacheKey = $"agents:single:{id:N}";
        var agent = await GetOrSetCacheAsync(cacheKey, () => _agentRepo.GetByIdAsync(id));
        if (agent is not null) { var g = await GetOnlineGraceSecondsForSiteAsync(agent.SiteId); ApplyEffectiveStatus(agent, g); }
        return agent is null ? NotFound() : Ok(agent);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAgentRequest request)
    {
        var agent = new Agent { SiteId = request.SiteId, Hostname = request.Hostname, DisplayName = request.DisplayName, OperatingSystem = request.OperatingSystem, OsVersion = request.OsVersion, AgentVersion = request.AgentVersion };
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
        agent.SiteId = request.SiteId; agent.Hostname = request.Hostname; agent.DisplayName = request.DisplayName;
        await _agentRepo.UpdateAsync(agent);
        await InvalidateAgentScopeCachesAsync(agent.SiteId, previousSiteId);
        return Ok(agent);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (!string.IsNullOrWhiteSpace(agent?.MeshCentralNodeId))
        {
            try { await _meshCentralApiService.RemoveDeviceAsync(agent.MeshCentralNodeId, HttpContext.RequestAborted); }
            catch (Exception ex) { _logger.LogWarning(ex, "MeshCentral cleanup failed for agent {AgentId} node {NodeId}", id, agent.MeshCentralNodeId); }
        }
        await _agentRepo.DeleteAsync(id);
        if (agent is not null) await InvalidateAgentScopeCachesAsync(agent.SiteId, null, id);
        return NoContent();
    }

    [HttpGet("{id:guid}/custom-fields")]
    public async Task<IActionResult> GetCustomFieldValues(Guid id, [FromQuery] bool includeSecrets = true, CancellationToken ct = default)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();
        return Ok(await _customFieldService.GetValuesAsync(CustomFieldScopeType.Agent, id, includeSecrets, ct));
    }

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    public async Task<IActionResult> UpsertCustomFieldValue(Guid id, Guid definitionId, [FromBody] UpsertAgentCustomFieldValueRequest request, CancellationToken ct = default)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();
        try
        {
            return Ok(await _customFieldService.UpsertValueAsync(
                new UpsertCustomFieldValueInput(definitionId, CustomFieldScopeType.Agent, id, request.Value.GetRawText(), HttpContext.Items["Username"] as string ?? "api"), ct));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}

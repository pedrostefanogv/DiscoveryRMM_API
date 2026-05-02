using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent app store, custom fields, deploy tokens, update, and MeshCentral support endpoints.
/// </summary>
public partial class AgentAuthController
{
    /// <summary>
    /// Validates agent credentials and returns agent identity info.
    /// Used by the agent to verify its token is still valid and to resolve
    /// clientId/siteId for scoped operations (tickets, support, P2P, etc).
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetAgentIdentity()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: true);
        if (agent is null)
            return blocked ?? NotFound(new { error = "Agent not found." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);

        return Ok(new
        {
            agentId = agent.Id,
            clientId = site?.ClientId,
            siteId = agent.SiteId,
            hostname = agent.Hostname,
            displayName = agent.DisplayName
        });
    }

    [HttpGet("me/app-store")]
    public async Task<IActionResult> GetAppStoreEffective(
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var site = await _siteRepo.GetByIdAsync(agent!.SiteId);
        if (site is null) return NotFound(new { error = "Site not found for this agent." });

        var effective = await _appStoreService.GetEffectiveAppsAsync(site.ClientId, site.Id, agent.Id, installationType, cancellationToken);
        return Ok(new { installationType, count = effective.Count, items = effective });
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
        if (blocked is not null) return blocked;

        return Ok(await _customFieldService.GetRuntimeValuesForAgentAsync(agentId, taskId, scriptId, cancellationToken));
    }

    [HttpPost("me/custom-fields/collected")]
    public async Task<IActionResult> UpsertCollectedCustomField(
        [FromBody] AgentUpsertCustomFieldValueRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        try
        {
            var result = await _customFieldService.UpsertAgentCollectedValueAsync(agentId,
                new AgentCustomFieldCollectedValueInput(request.DefinitionId, request.Name, request.Value.GetRawText(),
                    request.TaskId, request.ScriptId, "agent"), cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("me/zero-touch/deploy-token")]
    public async Task<IActionResult> IssueZeroTouchDeployToken()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var resolved = await _configResolver.ResolveForSiteAsync(agent!.SiteId);
        if (!resolved.DiscoveryEnabled)
            return StatusCode(403, new { error = "Zero-touch provisioning (discovery) is disabled for this site." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return NotFound(new { error = "Site not found." });

        var (token, rawToken) = await _deployTokenService.CreateZeroTouchTokenAsync(site.ClientId, site.Id);
        return Ok(new { token = rawToken, tokenId = token.Id, expiresAt = token.ExpiresAt, maxUses = token.MaxUses });
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
        if (blocked is not null) return blocked;

        try
        {
            return Ok(await _agentUpdateService.GetManifestAsync(agentId,
                new AgentUpdateManifestRequest(currentVersion, platform, architecture, artifactType), cancellationToken));
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
        if (blocked is not null) return blocked;

        try
        {
            var payload = await _agentUpdateService.GetPresignedDownloadUrlAsync(agentId,
                new AgentUpdateDownloadRequest(releaseId, version, platform, architecture, artifactType), cancellationToken);

            if (payload is null) return NotFound(new { error = "No applicable update artifact is available for this agent." });

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
        if (blocked is not null) return blocked;

        try
        {
            return Ok(await _agentUpdateService.RecordEventAsync(agentId, request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("me/sync/ping/{eventId:guid}/ack")]
    public async Task<IActionResult> AckSyncPing(Guid eventId, [FromBody] SyncPingAckRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        if (string.IsNullOrWhiteSpace(request.Revision))
            return BadRequest(new { error = "Revision is required." });

        var delivery = await _syncPingDeliveryRepository.UpsertAckAsync(eventId, agentId, request, request.ProcessedAt ?? DateTime.UtcNow);
        return Ok(new SyncPingAckResponse { Acknowledged = true, EventId = eventId, DeliveryId = delivery.Id });
    }
}

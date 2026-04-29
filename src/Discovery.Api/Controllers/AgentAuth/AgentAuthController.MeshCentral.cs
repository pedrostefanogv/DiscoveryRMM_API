using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent MeshCentral support endpoints: embed URL generation and install instructions.
/// </summary>
public partial class AgentAuthController
{
    [HttpPost("me/support/meshcentral/embed-url")]
    public async Task<IActionResult> CreateMeshCentralEmbedUrl([FromBody] AgentMeshCentralEmbedRequest? request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var site = await _siteRepo.GetByIdAsync(agent!.SiteId);
        if (site is null) return NotFound(new { error = "Site not found." });

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.SupportEnabled;
        if (!meshCentralEnabledEffective)
            return StatusCode(403, new { error = "MeshCentral support is disabled for this scope." });

        var desiredViewMode = request?.ViewMode ?? 11;
        var effectiveMeshNodeId = string.IsNullOrWhiteSpace(agent.MeshCentralNodeId)
            ? request?.MeshNodeId
            : agent.MeshCentralNodeId;

        try
        {
            var embed = await _meshCentralEmbeddingService.GenerateAgentEmbedUrlAsync(
                agent, site.ClientId, desiredViewMode, request?.HideMask,
                effectiveMeshNodeId, request?.GotoDeviceName, HttpContext.RequestAborted);

            return Ok(new
            {
                url = embed.Url, expiresAtUtc = embed.ExpiresAtUtc,
                viewMode = embed.ViewMode, hideMask = embed.HideMask,
                agentId = agent.Id, meshNodeId = effectiveMeshNodeId,
                clientId = site.ClientId, siteId = site.Id
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }

    [HttpGet("me/support/meshcentral/install")]
    public async Task<IActionResult> GetMeshCentralInstall()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var site = await _siteRepo.GetByIdAsync(agent!.SiteId);
        if (site is null) return NotFound(new { error = "Site not found." });

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.SupportEnabled;
        if (!meshCentralEnabledEffective)
            return StatusCode(403, new { error = "MeshCentral support is disabled for this scope." });

        var client = await _clientRepo.GetByIdAsync(site.ClientId);
        if (client is null) return NotFound(new { error = "Client not found." });

        try
        {
            var instructions = await _meshCentralApiService.ProvisionInstallAsync(client, site, string.Empty, HttpContext.RequestAborted);
            return Ok(instructions);
        }
        catch (InvalidOperationException)
        {
            try
            {
                var fallback = await _meshCentralProvisioningService.BuildInstallInstructionsAsync(client, site, string.Empty, meshCentralEnabledEffective);
                return Ok(fallback);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(503, new { error = ex.Message });
            }
        }
    }
}

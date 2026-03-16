using Meduza.Core.Interfaces;
using Meduza.Core.Enums.Identity;
using Meduza.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/meshcentral")]
[RequireUserAuth]
public class MeshCentralController : ControllerBase
{
    private readonly IClientRepository _clientRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly IMeshCentralEmbeddingService _meshCentralEmbeddingService;
    private readonly IMeshCentralIdentitySyncService _meshCentralIdentitySyncService;

    public MeshCentralController(
        IClientRepository clientRepository,
        ISiteRepository siteRepository,
        IAgentRepository agentRepository,
        IConfigurationResolver configurationResolver,
        IMeshCentralEmbeddingService meshCentralEmbeddingService,
        IMeshCentralIdentitySyncService meshCentralIdentitySyncService)
    {
        _clientRepository = clientRepository;
        _siteRepository = siteRepository;
        _agentRepository = agentRepository;
        _configurationResolver = configurationResolver;
        _meshCentralEmbeddingService = meshCentralEmbeddingService;
        _meshCentralIdentitySyncService = meshCentralIdentitySyncService;
    }

    /// <summary>
    /// Endpoint de preparacao para autenticacao unificada: gera URL de embedding para usuario MeshCentral informado.
    /// </summary>
    [HttpPost("embed-url")]
    public async Task<IActionResult> CreateUserEmbedUrl([FromBody] MeshCentralUserEmbedRequest request)
    {
        var client = await _clientRepository.GetByIdAsync(request.ClientId);
        if (client is null)
            return NotFound(new { error = "Client not found." });

        var site = await _siteRepository.GetByIdAsync(request.SiteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        if (site.ClientId != request.ClientId)
            return BadRequest(new { error = "Site does not belong to informed client." });

        Guid? validatedAgentId = null;
        if (request.AgentId.HasValue)
        {
            var agent = await _agentRepository.GetByIdAsync(request.AgentId.Value);
            if (agent is null)
                return NotFound(new { error = "Agent not found." });

            if (agent.SiteId != request.SiteId)
                return BadRequest(new { error = "Agent does not belong to informed site." });

            validatedAgentId = agent.Id;
        }

        var resolved = await _configurationResolver.ResolveForSiteAsync(request.SiteId);
        if (!resolved.RemoteSupportMeshCentralEnabled)
            return StatusCode(403, new { error = "MeshCentral support is disabled for this scope." });

        var viewMode = request.ViewMode ?? 11;
        try
        {
            var embed = await _meshCentralEmbeddingService.GenerateUserEmbedUrlAsync(
                request.MeshUsername,
                request.ClientId,
                request.SiteId,
                validatedAgentId,
                viewMode,
                request.HideMask,
                request.MeshNodeId,
                request.GotoDeviceName,
                HttpContext.RequestAborted);

            return Ok(new
            {
                url = embed.Url,
                expiresAtUtc = embed.ExpiresAtUtc,
                viewMode = embed.ViewMode,
                hideMask = embed.HideMask,
                clientId = request.ClientId,
                siteId = request.SiteId,
                agentId = validatedAgentId,
                meshUsername = request.MeshUsername
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Executa backfill/reconciliação de usuários Meduza para MeshCentral.
    /// applyChanges=false executa dry-run sem persistir alterações remotas.
    /// </summary>
    [HttpPost("identity-sync/backfill")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RunIdentitySyncBackfill([FromBody] MeshCentralIdentityBackfillRequest request)
    {
        var report = await _meshCentralIdentitySyncService.RunBackfillAsync(
            request.ClientId,
            request.SiteId,
            request.ApplyChanges,
            HttpContext.RequestAborted);

        return Ok(report);
    }
}

public record MeshCentralUserEmbedRequest(
    Guid ClientId,
    Guid SiteId,
    string MeshUsername,
    Guid? AgentId = null,
    int? ViewMode = null,
    int? HideMask = null,
    string? MeshNodeId = null,
    string? GotoDeviceName = null);

public record MeshCentralIdentityBackfillRequest(
    bool ApplyChanges = false,
    Guid? ClientId = null,
    Guid? SiteId = null);

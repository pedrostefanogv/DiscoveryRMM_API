using Asp.Versioning;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints autenticados por JWT de Agent para a aba "Arquivos" do painel local.
/// Respeita o kill switch herdável <c>FilesTabEnabled</c> (Global → Client → Site)
/// e a herança aditiva de arquivos.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/agent-files")]
public class AgentFilesController(
    IManagedFileService fileService,
    IAgentRepository agentRepository,
    ISiteRepository siteRepository,
    IConfigurationResolver configResolver) : ControllerBase
{
    private bool TryGetAgentId(out Guid agentId)
    {
        if (HttpContext.Items["AgentId"] is Guid id)
        {
            agentId = id;
            return true;
        }
        agentId = Guid.Empty;
        return false;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var agent = await agentRepository.GetByIdAsync(agentId);
        if (agent is null)
            return Unauthorized(new { error = "Agent não encontrado." });

        var site = await siteRepository.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Unauthorized(new { error = "Site do agent não encontrado." });

        var resolved = await configResolver.ResolveForSiteAsync(agent.SiteId);
        if (!resolved.FilesTabEnabled)
            return Ok(Array.Empty<object>());

        var files = await fileService.ListForAgentAsync(site.ClientId, agent.SiteId, ct);
        return Ok(files);
    }

    [HttpPost("{id:guid}/download-url")]
    public async Task<IActionResult> GetDownloadUrl(Guid id, CancellationToken ct)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent não autenticado." });

        var agent = await agentRepository.GetByIdAsync(agentId);
        if (agent is null)
            return Unauthorized(new { error = "Agent não encontrado." });

        var site = await siteRepository.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Unauthorized(new { error = "Site do agent não encontrado." });

        var resolved = await configResolver.ResolveForSiteAsync(agent.SiteId);
        if (!resolved.FilesTabEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "A aba Arquivos está desabilitada para este site." });

        try
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var ua = Request.Headers.UserAgent.ToString();
            return Ok(await fileService.GetAgentDownloadUrlAsync(id, agent.Id, site.ClientId, agent.SiteId, ip, ua, ct));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}

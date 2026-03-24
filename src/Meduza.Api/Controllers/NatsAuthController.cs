using Meduza.Api.Filters;
using Meduza.Core.DTOs;
using Meduza.Core.Enums.Identity;
using Meduza.Core.Interfaces;
using Meduza.Core.Interfaces.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/nats-auth")]
public class NatsAuthController : ControllerBase
{
    private readonly INatsCredentialsService _credentialsService;
    private readonly IPermissionService _permissionService;
    private readonly ISiteRepository _siteRepository;

    public NatsAuthController(
        INatsCredentialsService credentialsService,
        IPermissionService permissionService,
        ISiteRepository siteRepository)
    {
        _credentialsService = credentialsService;
        _permissionService = permissionService;
        _siteRepository = siteRepository;
    }

    [HttpPost("user/credentials")]
    [RequireUserAuth]
    public async Task<IActionResult> IssueUserCredentials([FromBody] NatsCredentialsRequest? request, CancellationToken ct)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var scopeAccess = await _permissionService.GetScopeAccessAsync(userId, ResourceType.Dashboard, ActionType.View);
        if (!scopeAccess.HasGlobalAccess && scopeAccess.AllowedClientIds.Count == 0 && scopeAccess.AllowedSiteIds.Count == 0)
            return Forbid();
        var clientId = request?.ClientId;
        var siteId = request?.SiteId;

        if (siteId.HasValue)
        {
            var site = await _siteRepository.GetByIdAsync(siteId.Value);
            if (site is null)
                return NotFound(new { error = "Site not found." });

            if (clientId.HasValue && site.ClientId != clientId.Value)
                return BadRequest(new { error = "Site does not belong to the specified client." });

            clientId ??= site.ClientId;
        }

        try
        {
            var credentials = await _credentialsService.IssueForUserAsync(userId, scopeAccess, clientId, siteId, ct);
            return Ok(credentials);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }
}

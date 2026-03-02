using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/agent-install")]
public class AgentInstallController : ControllerBase
{
    private readonly IAgentRepository _agentRepo;
    private readonly IAgentAuthService _authService;
    private readonly IDeployTokenService _deployTokenService;

    public AgentInstallController(
        IAgentRepository agentRepo,
        IAgentAuthService authService,
        IDeployTokenService deployTokenService)
    {
        _agentRepo = agentRepo;
        _authService = authService;
        _deployTokenService = deployTokenService;
    }

    [HttpPost("{agentId:guid}/token")]
    public async Task<IActionResult> CreateInstallToken(Guid agentId)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new { error = "Missing or invalid Authorization header." });
        }

        var rawDeployToken = authHeader["Bearer ".Length..].Trim();

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound();

        var deployToken = await _deployTokenService.TryUseTokenAsync(rawDeployToken);
        if (deployToken is null)
            return Unauthorized(new { error = "Invalid or expired deploy token." });

        var (token, rawToken) = await _authService.CreateTokenAsync(agentId, "Install token", null);
        return Ok(new { Token = rawToken, Id = token.Id, ExpiresAt = token.ExpiresAt });
    }
}

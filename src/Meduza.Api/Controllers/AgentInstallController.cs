using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/agent-install")]
[AllowAnonymous]
public class AgentInstallController : ControllerBase
{
    private readonly IAgentRepository _agentRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IAgentAuthService _authService;
    private readonly IDeployTokenService _deployTokenService;

    public AgentInstallController(
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IAgentAuthService authService,
        IDeployTokenService deployTokenService)
    {
        _agentRepo = agentRepo;
        _siteRepo = siteRepo;
        _authService = authService;
        _deployTokenService = deployTokenService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterAgentInstallRequest request)
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new { error = "Missing or invalid Authorization header." });
        }

        var rawDeployToken = authHeader["Bearer ".Length..].Trim();
        var deployToken = await _deployTokenService.TryUseTokenAsync(rawDeployToken);
        if (deployToken is null)
            return Unauthorized(new { error = "Invalid or expired deploy token." });

        if (!deployToken.SiteId.HasValue || !deployToken.ClientId.HasValue)
            return Unauthorized(new { error = "Deploy token is not scoped to client/site." });

        var site = await _siteRepo.GetByIdAsync(deployToken.SiteId.Value);
        if (site is null || site.ClientId != deployToken.ClientId.Value)
            return Unauthorized(new { error = "Deploy token scope is invalid for current site/client." });

        var agent = await _agentRepo.CreateAsync(new Meduza.Core.Entities.Agent
        {
            SiteId = site.Id,
            Hostname = request.Hostname,
            DisplayName = request.DisplayName,
            OperatingSystem = request.OperatingSystem,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion,
            MacAddress = request.MacAddress
        });

        var (token, rawToken) = await _authService.CreateTokenAsync(agent.Id, "Install token", null);

        return Ok(new
        {
            AgentId = agent.Id,
            ClientId = deployToken.ClientId,
            SiteId = deployToken.SiteId,
            Token = rawToken,
            TokenId = token.Id,
            ExpiresAt = token.ExpiresAt
        });
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

        if (!deployToken.SiteId.HasValue || !deployToken.ClientId.HasValue)
            return Unauthorized(new { error = "Deploy token is not scoped to client/site." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null || site.Id != deployToken.SiteId.Value || site.ClientId != deployToken.ClientId.Value)
            return Unauthorized(new { error = "Deploy token scope does not allow this agent." });

        var (token, rawToken) = await _authService.CreateTokenAsync(agentId, "Install token", null);
        return Ok(new { Token = rawToken, Id = token.Id, ExpiresAt = token.ExpiresAt });
    }
}

public record RegisterAgentInstallRequest(
    string Hostname,
    string? DisplayName,
    string? OperatingSystem,
    string? OsVersion,
    string? AgentVersion,
    string? MacAddress);

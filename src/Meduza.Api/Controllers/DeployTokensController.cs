using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/deploy-tokens")]
public class DeployTokensController : ControllerBase
{
    private readonly IDeployTokenService _deployTokenService;
    private readonly ISiteRepository _siteRepository;

    public DeployTokensController(IDeployTokenService deployTokenService, ISiteRepository siteRepository)
    {
        _deployTokenService = deployTokenService;
        _siteRepository = siteRepository;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDeployTokenRequest request)
    {
        var site = await _siteRepository.GetByIdAsync(request.SiteId);
        if (site is null)
            return BadRequest(new { error = "Site not found." });

        if (site.ClientId != request.ClientId)
            return BadRequest(new { error = "Site does not belong to informed client." });

        var (token, rawToken) = await _deployTokenService.CreateTokenAsync(
            request.ClientId,
            request.SiteId,
            request.Description,
            request.ExpiresInHours,
            request.MultiUse ?? false);

        return Ok(new
        {
            Token = rawToken,
            Id = token.Id,
            ClientId = token.ClientId,
            SiteId = token.SiteId,
            ExpiresAt = token.ExpiresAt,
            MaxUses = token.MaxUses
        });
    }
}

public record CreateDeployTokenRequest(Guid ClientId, Guid SiteId, string? Description, int? ExpiresInHours, bool? MultiUse);

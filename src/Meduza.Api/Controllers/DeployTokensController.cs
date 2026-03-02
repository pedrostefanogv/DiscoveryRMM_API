using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/deploy-tokens")]
public class DeployTokensController : ControllerBase
{
    private readonly IDeployTokenService _deployTokenService;

    public DeployTokensController(IDeployTokenService deployTokenService)
    {
        _deployTokenService = deployTokenService;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDeployTokenRequest request)
    {
        var (token, rawToken) = await _deployTokenService.CreateTokenAsync(
            request.Description,
            request.ExpiresInHours,
            request.MultiUse ?? false);

        return Ok(new
        {
            Token = rawToken,
            Id = token.Id,
            ExpiresAt = token.ExpiresAt,
            MaxUses = token.MaxUses
        });
    }
}

public record CreateDeployTokenRequest(string? Description, int? ExpiresInHours, bool? MultiUse);

using Discovery.Core.DTOs.ApiTokens;
using Discovery.Core.Interfaces.Auth;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/api-tokens")]
[RequireUserAuth]
public class ApiTokensController : ControllerBase
{
    private readonly IApiTokenService _tokenService;

    public ApiTokensController(IApiTokenService tokenService)
    {
        _tokenService = tokenService;
    }

    /// <summary>Lista os tokens do usuário autenticado (sem exposição da accessKey).</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var tokens = await _tokenService.GetByUserAsync(userId);
        return Ok(tokens);
    }

    /// <summary>
    /// Cria um novo token de API.
    /// A accessKey retornada é exibida apenas uma vez — não pode ser recuperada depois.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiTokenRequestDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var result = await _tokenService.CreateTokenAsync(userId, dto.Name, dto.ExpiresAt);
        return CreatedAtAction(nameof(List), null, result);
    }

    /// <summary>Revoga (desativa) um token de API.</summary>
    [HttpDelete("{tokenId:guid}")]
    public async Task<IActionResult> Revoke(Guid tokenId)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var revoked = await _tokenService.RevokeAsync(tokenId, userId);
        return revoked ? NoContent() : NotFound();
    }
}

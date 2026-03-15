using Meduza.Core.DTOs.Auth;
using Meduza.Core.DTOs.Mfa;
using Meduza.Core.Interfaces.Auth;
using Meduza.Core.Interfaces.Security;
using Meduza.Api.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly IUserAuthService _authService;
    private readonly IFido2Service _fido2Service;
    private readonly IUserMfaKeyRepository _mfaKeyRepo;

    public AuthController(
        IUserAuthService authService,
        IFido2Service fido2Service,
        IUserMfaKeyRepository mfaKeyRepo)
    {
        _authService = authService;
        _fido2Service = fido2Service;
        _mfaKeyRepo = mfaKeyRepo;
    }

    /// <summary>
    /// Etapa 1 do login: valida login+senha e retorna um mfaPendingToken (se MFA configurado)
    /// ou um mfaSetupToken (se MFA ainda não configurado).
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto dto)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
        var result = await _authService.LoginAsync(dto.LoginOrEmail, dto.Password, ip, ua);

        return Ok(result);
    }

    /// <summary>
    /// Etapa 2a — MFA via FIDO2: inicia o desafio de asserção.
    /// Requer header: Authorization: Bearer {mfaPendingToken}
    /// </summary>
    [HttpPost("mfa/fido2/begin")]
    [RequireMfaPending]
    public async Task<IActionResult> BeginFido2Assertion()
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var activeKeys = await _mfaKeyRepo.GetActiveByUserIdAsync(userId);
        var optionsJson = await _fido2Service.BeginAssertionAsync(userId, activeKeys);
        return Ok(new { options = optionsJson });
    }

    /// <summary>
    /// Etapa 2b — MFA via FIDO2: valida a resposta e emite a sessão completa (access + refresh tokens).
    /// </summary>
    [HttpPost("mfa/fido2/complete")]
    [RequireMfaPending]
    public async Task<IActionResult> CompleteFido2Assertion([FromBody] CompleteFido2AssertionDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();

        var activeKeys = await _mfaKeyRepo.GetActiveByUserIdAsync(userId);
        var result = await _fido2Service.CompleteAssertionAsync(userId, dto.AssertionResponseJson, activeKeys);
        if (!result.Success)
            return Unauthorized(new { message = result.ErrorMessage ?? "MFA inválido." });

        await _mfaKeyRepo.UpdateSignCountAsync(result.KeyId, result.NewSignCount);
        await _mfaKeyRepo.UpdateLastUsedAsync(result.KeyId);

        var session = await _authService.IssueFullSessionAsync(userId, true, ip, ua);
        return Ok(session);
    }

    /// <summary>
    /// Renova o par de tokens usando o refresh token.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequestDto dto)
    {
        var result = await _authService.RefreshAsync(dto.RefreshToken);
        if (result is null)
            return Unauthorized(new { message = "Refresh token inválido ou expirado." });

        return Ok(result);
    }

    /// <summary>
    /// Encerra a sessão atual, revogando o refresh token.
    /// </summary>
    [HttpPost("logout")]
    [RequireUserAuth]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequestDto dto)
    {
        if (!HttpContext.Items.TryGetValue("SessionId", out var sessionIdObj) ||
            !Guid.TryParse(sessionIdObj?.ToString(), out var sessionId))
        {
            return BadRequest(new { message = "Sessão inválida para logout." });
        }

        await _authService.LogoutAsync(sessionId);
        return NoContent();
    }
}

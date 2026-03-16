using Meduza.Core.DTOs.Auth;
using Meduza.Core.DTOs.Mfa;
using Meduza.Core.Entities.Security;
using Meduza.Core.Enums.Identity;
using Meduza.Core.Enums.Security;
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
    private readonly IOtpService _otpService;
    private readonly IUserMfaKeyRepository _mfaKeyRepo;
    private readonly ISecretProtector _secretProtector;

    public AuthController(
        IUserAuthService authService,
        IFido2Service fido2Service,
        IOtpService otpService,
        IUserMfaKeyRepository mfaKeyRepo,
        ISecretProtector secretProtector)
    {
        _authService = authService;
        _fido2Service = fido2Service;
        _otpService = otpService;
        _mfaKeyRepo = mfaKeyRepo;
        _secretProtector = secretProtector;
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
        var requirement = await _authService.GetEffectiveMfaRequirementAsync(userId);
        if (requirement == RoleMfaRequirement.Totp)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Esta conta exige MFA via OTP para login." });

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
        var requirement = await _authService.GetEffectiveMfaRequirementAsync(userId);
        if (requirement == RoleMfaRequirement.Totp)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Esta conta exige MFA via OTP para login." });

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
    /// Etapa 2b — MFA via OTP/TOTP: valida o código e emite a sessão completa.
    /// </summary>
    [HttpPost("mfa/otp/complete")]
    [RequireMfaPending]
    public async Task<IActionResult> CompleteOtpAssertion([FromBody] CompleteOtpAssertionDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var requirement = await _authService.GetEffectiveMfaRequirementAsync(userId);
        if (requirement == RoleMfaRequirement.Fido2)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Esta conta exige MFA via chave de segurança (FIDO2)." });

        if (string.IsNullOrWhiteSpace(dto.Code))
            return BadRequest(new { message = "Código OTP é obrigatório." });

        var activeKeys = await _mfaKeyRepo.GetActiveByUserIdAsync(userId);
        var otpKeys = activeKeys
            .Where(k => k.KeyType == MfaKeyType.Totp && !string.IsNullOrWhiteSpace(k.OtpSecretEncrypted))
            .ToList();

        if (otpKeys.Count == 0)
            return Unauthorized(new { message = "Nenhuma credencial OTP ativa encontrada para o usuário." });

        var normalizedCode = dto.Code.Trim();
        UserMfaKey? matchedKey = null;
        foreach (var key in otpKeys)
        {
            var secret = _secretProtector.UnprotectOrSelf(key.OtpSecretEncrypted);
            if (_otpService.ValidateTotp(secret, normalizedCode))
            {
                matchedKey = key;
                break;
            }
        }

        if (matchedKey is null)
            return Unauthorized(new { message = "OTP inválido." });

        await _mfaKeyRepo.UpdateLastUsedAsync(matchedKey!.Id);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
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
    /// Conclui o onboarding de primeiro acesso (troca de login/perfil/senha).
    /// Requer token mfa_setup ou sessão completa.
    /// </summary>
    [HttpPost("first-access/complete")]
    [RequireMfaSetupOrFullSession]
    public async Task<IActionResult> CompleteFirstAccess([FromBody] CompleteFirstAccessRequestDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        try
        {
            await _authService.CompleteFirstAccessAsync(userId, dto);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        return Ok(new { message = "Primeiro acesso concluído. Finalize o cadastro do MFA para liberar o login completo." });
    }

    /// <summary>
    /// Retorna status de onboarding para o frontend decidir próxima tela/etapa.
    /// Requer token mfa_setup ou sessão completa.
    /// </summary>
    [HttpGet("first-access/status")]
    [RequireMfaSetupOrFullSession]
    public async Task<IActionResult> GetFirstAccessStatus()
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var status = await _authService.GetFirstAccessStatusAsync(userId);
        return Ok(status);
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

using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.Cqrs.Auth.Queries;
using Discovery.Core.DTOs.Auth;
using Discovery.Core.DTOs.Mfa;
using Discovery.Api.Filters;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/auth")]
[AllowAnonymous]
public class AuthController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    private IActionResult UnauthorizedAuth(string message, string code = "auth_failed")
        => Unauthorized(new { code, message });

    private IActionResult ForbiddenAuth(string message, string code = "auth_forbidden")
        => StatusCode(StatusCodes.Status403Forbidden, new { code, message });

    // ── Login / Refresh / Logout (CQRS via MediatR) ──────────────────────

    /// <summary>
    /// Etapa 1 do login: valida login+senha e retorna um mfaPendingToken (se MFA configurado)
    /// ou um mfaSetupToken (se MFA ainda não configurado).
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto dto)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
        var query = new LoginQuery(dto.LoginOrEmail, dto.Password, ip, ua);
        var result = await _mediator.Send(query);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "Unauthorized"
                ? UnauthorizedAuth(errors[0].Message, "invalid_credentials")
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Renova o par de tokens usando o refresh token.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequestDto dto)
    {
        var result = await _mediator.Send(new RefreshTokenQuery(dto.RefreshToken));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "Unauthorized"
                ? UnauthorizedAuth(errors[0].Message, "refresh_invalid")
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Encerra a sessão atual, revogando o refresh token.
    /// </summary>
    [HttpPost("logout")]
    [RequireUserAuth]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequestDto dto)
    {
        var result = await _mediator.Send(new LogoutCommand(dto.RefreshToken));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── MFA FIDO2 (CQRS via MediatR) ────────────────────────────────────

    /// <summary>
    /// Etapa 2a — MFA via FIDO2: inicia o desafio de asserção.
    /// Requer header: Authorization: Bearer {mfaPendingToken}
    /// </summary>
    [HttpPost("mfa/fido2/begin")]
    [RequireMfaPending]
    public async Task<IActionResult> BeginFido2Assertion()
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var result = await _mediator.Send(new BeginFido2AssertionQuery(userId));
        return result.Match<IActionResult>(
            success: r => Ok(new { options = r.OptionsJson }),
            failure: errors => errors[0].Code == "Forbidden"
                ? ForbiddenAuth(errors[0].Message, "mfa_method_mismatch")
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
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
        var cmd = new CompleteFido2AssertionCommand(userId, dto.AssertionResponseJson, ip, ua);
        var result = await _mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code switch
            {
                "Forbidden" => ForbiddenAuth(errors[0].Message, "mfa_method_mismatch"),
                "Unauthorized" => UnauthorizedAuth(errors[0].Message, "mfa_invalid"),
                _ => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
            });
    }

    // ── MFA OTP/TOTP (CQRS via MediatR) ──────────────────────────────────

    /// <summary>
    /// Etapa 2b — MFA via OTP/TOTP: valida o código e emite a sessão completa.
    /// </summary>
    [HttpPost("mfa/otp/complete")]
    [RequireMfaPending]
    public async Task<IActionResult> CompleteOtpAssertion([FromBody] CompleteOtpAssertionDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
        var cmd = new CompleteOtpAssertionCommand(userId, dto.Code, ip, ua);
        var result = await _mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code switch
            {
                "Forbidden" => ForbiddenAuth(errors[0].Message, "mfa_method_mismatch"),
                "Unauthorized" => UnauthorizedAuth(errors[0].Message, "otp_invalid"),
                _ => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
            });
    }

    // ── First Access / Onboarding (CQRS via MediatR) ─────────────────────

    /// <summary>
    /// Conclui o onboarding de primeiro acesso (troca de login/perfil/senha).
    /// Requer token mfa_setup ou sessão completa.
    /// </summary>
    [HttpPost("first-access/complete")]
    [RequireMfaSetupOrFullSession]
    public async Task<IActionResult> CompleteFirstAccess([FromBody] CompleteFirstAccessRequestDto dto)
    {
        var userId = (Guid)HttpContext.Items["UserId"]!;
        var cmd = new CompleteFirstAccessCommand(userId, dto);
        var result = await _mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: _ => Ok(new { message = "Primeiro acesso concluído. Finalize o cadastro do MFA para liberar o login completo." }),
            failure: errors => errors[0].Code switch
            {
                "Unauthorized" => UnauthorizedAuth(errors[0].Message, "first_access_not_allowed"),
                _ => BadRequest(new { message = errors[0].Message })
            });
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
        var result = await _mediator.Send(new GetFirstAccessStatusQuery(userId));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}

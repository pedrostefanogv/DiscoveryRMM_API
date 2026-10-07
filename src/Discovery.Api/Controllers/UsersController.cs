using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.Cqrs.Users.Queries;
using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.DTOs.Users;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/users")]
public class UsersController(IMediator mediator, IUserAuthService userAuth) : ControllerBase
{
    private readonly IUserAuthService _userAuth = userAuth;

    private bool TryGetUserId(out Guid userId)
    {
        if (HttpContext.Items["UserId"] is Guid id)
        {
            userId = id;
            return true;
        }

        userId = Guid.Empty;
        return false;
    }

    private static IActionResult NotAuthenticated()
        => new UnauthorizedObjectResult(new { error = "Not authenticated." });

    [HttpGet]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetAll([FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new ListUsersQuery(cursor, limit));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetUserByIdQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Users, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateUserCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteUserCommand(id));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Próprio perfil ───────────────────────────────────────────────────────

    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser()
    {
        if (!TryGetUserId(out var userId))
            return NotAuthenticated();

        var result = await mediator.Send(new GetUserByIdQuery(userId));
        return result.ToActionResult();
    }

    /// <summary>Atualiza o próprio perfil (e-mail e nome completo).</summary>
    [HttpPut("me")]
    public async Task<IActionResult> UpdateMyProfile([FromBody] UpdateMyProfileDto dto)
    {
        if (!TryGetUserId(out var userId))
            return NotAuthenticated();

        var result = await mediator.Send(new UpdateMyProfileCommand(
            userId, dto.Email ?? string.Empty, dto.FullName ?? string.Empty));

        return result.ToActionResult();
    }

    /// <summary>
    /// Perfil de segurança do próprio usuário (contrato de GET /users/me/security).
    /// Antes devolvia um UserDto sem a propriedade <c>keys</c>, o que quebrava a
    /// ProfilePage do console web com "cannot read length of undefined".
    /// </summary>
    [HttpGet("me/security")]
    public async Task<IActionResult> GetCurrentUserSecurity()
    {
        if (!TryGetUserId(out var userId))
            return NotAuthenticated();

        var result = await mediator.Send(new GetMySecurityProfileQuery(userId));
        return result.ToActionResult();
    }

    /// <summary>Troca a própria senha (exige a senha atual).</summary>
    [HttpPost("me/change-password")]
    public async Task<IActionResult> ChangeMyPassword([FromBody] ChangePasswordDto dto)
    {
        if (!TryGetUserId(out var userId))
            return NotAuthenticated();

        var result = await mediator.Send(new ChangeUserPasswordCommand(
            userId,
            dto.CurrentPassword ?? string.Empty,
            dto.NewPassword ?? string.Empty,
            HttpContext.Items["SessionId"] as string));

        return result.ToActionResult();
    }

    // ── Administração de outro usuário ───────────────────────────────────────

    /// <summary>Redefine a senha de um usuário (não exige a senha atual).</summary>
    [HttpPost("{id:guid}/change-password")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> ChangePassword(Guid id, [FromBody] ChangePasswordDto dto)
    {
        var requestedBy = HttpContext.Items["Username"] as string;
        var result = await mediator.Send(new ResetUserPasswordCommand(id, dto.NewPassword ?? string.Empty, requestedBy));
        return result.ToActionResult();
    }

    /// <summary>
    /// Libera manualmente os bloqueios da conta (lockout de senha e de MFA).
    /// </summary>
    [HttpPost("{id:guid}/unlock")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> Unlock(Guid id)
    {
        try
        {
            await _userAuth.UnlockAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException)
        {
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = $"User {id} not found" } } });
        }
    }

    /// <summary>Marca o usuário para trocar a senha no próximo login.</summary>
    [HttpPost("{id:guid}/force-password-reset")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> ForcePasswordReset(Guid id)
    {
        var result = await mediator.Send(new ForcePasswordChangeCommand(id));
        return result.ToActionResult();
    }

    /// <summary>
    /// Estado de segurança (lockout de senha/MFA, obrigações de troca). Dados sensíveis
    /// de operação: exigem Users.Edit, não vazam mais no UserDto geral.
    /// </summary>
    [HttpGet("{id:guid}/security-state")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> GetUserSecurityState(Guid id)
    {
        var result = await mediator.Send(new GetUserSecurityStateQuery(id));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}/mfa/keys")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetUserMfaKeys(Guid id)
    {
        var result = await mediator.Send(new ListUserMfaKeysQuery(id));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}/mfa")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RevokeUserMfa(Guid id)
    {
        var result = await mediator.Send(new RevokeUserMfaCommand(id));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}/mfa/keys/{keyId:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RevokeUserMfaKey(Guid id, Guid keyId)
    {
        var result = await mediator.Send(new RevokeUserMfaKeyCommand(id, keyId));
        return result.ToActionResult();
    }
}

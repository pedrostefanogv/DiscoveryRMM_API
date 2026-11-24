using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.Cqrs.Users.Queries;
using Discovery.Core.Cqrs.Auth.Commands;
using Discovery.Core.DTOs.Users;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/users")]
public class UsersController(
    IMediator mediator,
    IUserAuthService userAuth,
    Discovery.Core.Interfaces.Security.IIdentityAuditService audit,
    IScopeContext scopeContext,
    IUserScopedQueryRepository scopedUsers,
    Discovery.Core.Interfaces.Security.IUserSessionRepository sessions) : ControllerBase
{
    private readonly IUserAuthService _userAuth = userAuth;
    private readonly Discovery.Core.Interfaces.Security.IIdentityAuditService _audit = audit;
    private readonly IScopeContext _scopeContext = scopeContext;
    private readonly IUserScopedQueryRepository _scopedUsers = scopedUsers;
    private readonly Discovery.Core.Interfaces.Security.IUserSessionRepository _sessions = sessions;

    /// <summary>
    /// Garante que o ALVO de uma operação direcionada está no escopo do ator.
    ///
    /// Por que isto existe: os endpoints de identidade passaram a usar
    /// <c>ScopeSource.AccessList</c> para que um administrador escopado a cliente
    /// pudesse administrar os próprios usuários. Mas <c>AccessList</c> só prova que
    /// o ator tem a permissão em ALGUM lugar — NÃO que ele pode tocar NAQUELE id.
    /// Sem esta guarda, um admin do cliente A leria/editava/desativava/resetava a
    /// senha (e revogava o MFA) de qualquer usuário da plataforma, inclusive de
    /// outros clientes e dos administradores globais.
    ///
    /// Retorna null quando permitido, ou o ActionResult de recusa.
    /// </summary>
    private async Task<IActionResult?> EnsureTargetInScopeAsync(Guid targetUserId, ActionType action)
    {
        var actorId = HttpContext.Items["UserId"] is Guid uid ? uid : Guid.Empty;
        _scopeContext.SetUserId(actorId);

        var access = await _scopeContext.GetAccessAsync(ResourceType.Users, action);
        if (access.HasGlobalAccess)
            return null;

        var inScope = await _scopedUsers.IsUserInScopeAsync(
            false, access.AllowedClientIds, access.AllowedSiteIds, targetUserId);

        // 404 (e não 403) para não confirmar a existência de um usuário de outro
        // tenant — mesma decisão do acesso remoto e das gravações de terminal.
        return inScope ? null : NotFound(new { message = "Usuário não encontrado." });
    }

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
    [RequirePermission(ResourceType.Users, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll([FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        // Escopo do ator aplicado na consulta (row-level security). Antes a listagem
        // devolvia TODOS os usuários para quem tivesse Users.View em qualquer escopo.
        var actorId = HttpContext.Items["UserId"] is Guid uid ? uid : Guid.Empty;
        _scopeContext.SetUserId(actorId);
        var access = await _scopeContext.GetAccessAsync(ResourceType.Users, ActionType.View);

        var result = await mediator.Send(new ListUsersQuery(
            cursor, limit, access.HasGlobalAccess, access.AllowedClientIds, access.AllowedSiteIds));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.View) is { } denied) return denied;

        var result = await mediator.Send(new GetUserByIdQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Users, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> Create([FromBody] CreateUserCommand cmd)
    {
        // O ator é injetado do contexto autenticado (nunca do corpo): o handler usa
        // para validar que ele pode conceder o vínculo inicial (groupId).
        var actorId = HttpContext.Items["UserId"] is Guid uid ? uid : Guid.Empty;
        var result = await mediator.Send(cmd with { RequestedByUserId = actorId });
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserCommand cmd)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Delete, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Delete) is { } denied) return denied;

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
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> ChangePassword(Guid id, [FromBody] ChangePasswordDto dto)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        var requestedBy = HttpContext.Items["Username"] as string;
        var result = await mediator.Send(new ResetUserPasswordCommand(id, dto.NewPassword ?? string.Empty, requestedBy));
        return result.ToActionResult();
    }

    /// <summary>
    /// Libera manualmente os bloqueios da conta (lockout de senha e de MFA).
    /// </summary>
    [HttpPost("{id:guid}/unlock")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Unlock(Guid id)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

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
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> ForcePasswordReset(Guid id)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        var result = await mediator.Send(new ForcePasswordChangeCommand(id));
        return result.ToActionResult();
    }

    /// <summary>
    /// Estado de segurança (lockout de senha/MFA, obrigações de troca). Dados sensíveis
    /// de operação: exigem Users.Edit, não vazam mais no UserDto geral.
    /// </summary>
    [HttpGet("{id:guid}/security-state")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> GetUserSecurityState(Guid id)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        var result = await mediator.Send(new GetUserSecurityStateQuery(id));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}/mfa/keys")]
    [RequirePermission(ResourceType.Users, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetUserMfaKeys(Guid id)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.View) is { } denied) return denied;

        var result = await mediator.Send(new ListUserMfaKeysQuery(id));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}/mfa")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> RevokeUserMfa(Guid id)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        var result = await mediator.Send(new RevokeUserMfaCommand(id));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}/mfa/keys/{keyId:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> RevokeUserMfaKey(Guid id, Guid keyId)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        var result = await mediator.Send(new RevokeUserMfaKeyCommand(id, keyId));
        return result.ToActionResult();
    }

    /// <summary>
    /// Histórico de auditoria do usuário: eventos de autenticação (login, falha,
    /// lockout, MFA) E mutações de identidade que o afetaram (criação, edição,
    /// reset de senha, vínculo a grupo, concessão de escopo).
    ///
    /// Antes da auditoria de 2026-11-07 nenhuma mutação de identidade era
    /// registrada — não havia como responder "quem concedeu este acesso e quando".
    /// Exige Users.Edit (dado sensível de operação), como o security-state.
    /// </summary>
    [HttpGet("{id:guid}/audit")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> GetUserAudit(Guid id, [FromQuery] int limit = 100)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        var rows = await _audit.GetByTargetAsync("User", id, limit);
        var bySubject = await _audit.GetByUserAsyncForAuditView(id, limit);
        var byActor = await _audit.GetByActorAsync(id, limit);

        // Une as três visões (alvo direto + sujeito + ator) sem duplicar, mais recente primeiro.
        var merged = rows.Concat(bySubject).Concat(byActor)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .OrderByDescending(r => r.OccurredAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(ToAuditDto)
            .ToList();

        return Ok(new { items = merged });
    }

    /// <summary>
    /// Linha do tempo consolidada de acessos e ações do usuário no sistema.
    /// </summary>
    [HttpGet("{id:guid}/activity")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> GetUserActivity(
        Guid id,
        [FromQuery] string? category = null,
        [FromQuery] int limit = 100)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        var result = await mediator.Send(new GetUserActivityQuery(id, category, limit));
        return result.ToActionResult();
    }

    /// <summary>
    /// Lista sessões recentes (ativas, revogadas e expiradas) do usuário sem expor hashes de tokens.
    /// </summary>
    [HttpGet("{id:guid}/sessions")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> GetUserSessions(Guid id, [FromQuery] int limit = 30)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        var rows = await _sessions.GetRecentByUserIdAsync(id, limit);
        var items = rows.Select(s => new UserSessionSummaryDto
        {
            Id = s.Id,
            CreatedAt = s.CreatedAt,
            ExpiresAt = s.ExpiresAt,
            RevokedAt = s.RevokedAt,
            IsActive = s.IsValid,
            IsExpired = s.IsExpired,
            IsRevoked = s.IsRevoked,
            MfaVerified = s.MfaVerified,
            IpAddress = s.IpAddress,
            UserAgent = s.UserAgent,
        }).ToList();

        return Ok(new { items });
    }

    /// <summary>
    /// Revoga uma sessão específica de um usuário.
    /// </summary>
    [HttpDelete("{id:guid}/sessions/{sessionId:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> RevokeUserSession(Guid id, Guid sessionId)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        var session = await _sessions.GetByIdAsync(sessionId);
        if (session is null || session.UserId != id)
            return NotFound(new { message = "Sessão não encontrada." });

        await _sessions.RevokeAsync(sessionId);

        var actorId = HttpContext.Items["UserId"] is Guid uid ? uid : Guid.Empty;
        var actorLogin = HttpContext.Items["Username"] as string;
        await _audit.RecordAsync(
            "identity.user_session_revoked",
            actorId,
            actorLogin,
            "User",
            id,
            id,
            after: new { SessionId = sessionId });

        return NoContent();
    }

    /// <summary>
    /// Revoga todas as sessões ativas de um usuário.
    /// </summary>
    [HttpDelete("{id:guid}/sessions")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> RevokeAllUserSessions(Guid id)
    {
        if (await EnsureTargetInScopeAsync(id, ActionType.Edit) is { } denied) return denied;

        await _sessions.RevokeAllByUserIdAsync(id);

        var actorId = HttpContext.Items["UserId"] is Guid uid ? uid : Guid.Empty;
        var actorLogin = HttpContext.Items["Username"] as string;
        await _audit.RecordAsync(
            "identity.user_sessions_revoked_all",
            actorId,
            actorLogin,
            "User",
            id,
            id);

        return NoContent();
    }

    private static object ToAuditDto(Discovery.Core.Entities.Security.AuthAuditLog l) => new
    {
        l.Id,
        l.UserId,
        l.EventType,
        l.Success,
        l.FailureReason,
        l.IpAddress,
        l.UserAgent,
        l.Detail,
        l.ActorUserId,
        l.ActorLogin,
        l.TargetType,
        l.TargetId,
        l.BeforeState,
        l.AfterState,
        l.OccurredAt
    };
}
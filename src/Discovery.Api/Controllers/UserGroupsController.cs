using Discovery.Api.Filters;
using Discovery.Core.Cqrs.UserGroups.Commands;
using Discovery.Core.Cqrs.UserGroups.Queries;
using Discovery.Core.DTOs.Groups;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Services.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/user-groups")]
public class UserGroupsController(
    IMediator mediator,
    IScopeContext scopeContext,
    IUserGroupRepository groups,
    ISiteRepository sites) : ControllerBase
{
    private readonly IScopeContext _scopeContext = scopeContext;
    private readonly IUserGroupRepository _groups = groups;
    private readonly ISiteRepository _sites = sites;

    /// <summary>
    /// Garante que o GRUPO alvo está dentro do escopo do ator.
    ///
    /// Mesma classe de bug dos usuários (bug #3 da revisão): os endpoints passaram a
    /// usar ScopeSource.AccessList para permitir administrador escopado a cliente,
    /// mas AccessList só prova que o ator tem a permissão em algum lugar — não que
    /// ele possa tocar NAQUELE grupo. Sem esta guarda um admin do cliente A lia,
    /// renomeava e EXCLUÍA qualquer grupo da plataforma, inclusive um grupo que
    /// carrega uma atribuição Global (destruindo acesso alheio).
    ///
    /// A regra é a mesma já usada nos handlers: o grupo é administrável somente se
    /// TODAS as suas atribuições couberem no escopo do ator. Delega ao handler de
    /// vínculo a mesma policy para não duplicar a comparação.
    /// </summary>
    private async Task<IActionResult?> EnsureGroupInScopeAsync(Guid groupId)
    {
        var actorId = HttpContext.Items["UserId"] is Guid uid ? uid : Guid.Empty;
        _scopeContext.SetUserId(actorId);

        var access = await _scopeContext.GetAccessAsync(ResourceType.Users, ActionType.Edit);
        if (access.HasGlobalAccess)
            return null;

        foreach (var assignment in await _groups.GetRolesForGroupAsync(groupId))
        {
            // O cliente PAI do site é necessário para a herança Client→Site: uma
            // concessão no nível de Client autoriza gerenciar um grupo cuja
            // atribuição é Site-scoped, e sem parentClientId isso seria negado.
            Guid? parentClientId = null;
            if (assignment.ScopeLevel == ScopeLevel.Site
                && assignment.ScopeId.HasValue
                && assignment.ScopeId.Value != Guid.Empty)
            {
                var site = await _sites.GetByIdAsync(assignment.ScopeId.Value);
                parentClientId = site?.ClientId;
            }

            if (!RoleGrantScopePolicy.CanGrant(access, assignment.ScopeLevel, assignment.ScopeId, parentClientId))
                return NotFound(new { message = "Grupo não encontrado." });
        }

        return null;
    }

    [HttpGet]
    [RequirePermission(ResourceType.Users, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll()
    {
        var result = await mediator.Send(new ListUserGroupsQuery());
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (await EnsureGroupInScopeAsync(id) is { } denied) return denied;
        var result = await mediator.Send(new GetUserGroupByIdQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Users, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> Create([FromBody] CreateUserGroupCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserGroupCommand cmd)
    {
        if (await EnsureGroupInScopeAsync(id) is { } denied) return denied;
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
        if (await EnsureGroupInScopeAsync(id) is { } denied) return denied;
        var result = await mediator.Send(new DeleteUserGroupCommand(id));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Membros ──────────────────────────────────────────────────────────────

    [HttpGet("{id:guid}/members")]
    [RequirePermission(ResourceType.Users, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetMembers(Guid id)
    {
        if (await EnsureGroupInScopeAsync(id) is { } denied) return denied;
        var result = await mediator.Send(new ListGroupMembersQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/members")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddGroupMemberDto dto)
    {
        if (await EnsureGroupInScopeAsync(id) is { } denied) return denied;
        // O ator vai no comando: o handler precisa dele para recusar vínculos que
        // concedam escopo fora do próprio (escalação entre clientes).
        var result = await mediator.Send(new AddGroupMemberCommand(id, dto.UserId, GetActorId()));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        if (await EnsureGroupInScopeAsync(id) is { } denied) return denied;
        var result = await mediator.Send(new RemoveGroupMemberCommand(id, userId, GetActorId()));
        return result.ToActionResult();
    }

    // ── Roles do grupo ───────────────────────────────────────────────────────

    [HttpGet("{id:guid}/roles")]
    [RequirePermission(ResourceType.Users, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetRoles(Guid id)
    {
        if (await EnsureGroupInScopeAsync(id) is { } denied) return denied;
        var result = await mediator.Send(new ListGroupRolesQuery(id));
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/roles")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] AddGroupRoleDto dto)
    {
        if (await EnsureGroupInScopeAsync(id) is { } denied) return denied;
        var result = await mediator.Send(
            new AssignGroupRoleCommand(id, dto.RoleId, dto.ScopeLevel, dto.ScopeId, GetActorId()));
        return result.ToActionResult();
    }

    [HttpDelete("{id:guid}/roles/{assignmentId:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> RemoveRole(Guid id, Guid assignmentId)
    {
        if (await EnsureGroupInScopeAsync(id) is { } denied) return denied;
        var result = await mediator.Send(new RemoveGroupRoleAssignmentCommand(id, assignmentId, GetActorId()));
        return result.ToActionResult();
    }

    /// <summary>
    /// Usuário autenticado que executa a operação. Necessário para a guarda de
    /// concessão (um admin escopado a cliente não pode conceder acesso fora do
    /// próprio escopo). Guid.Empty apenas quando não há sessão — o filtro
    /// RequireUserAuth já garante que isso não ocorre em produção.
    /// </summary>
    private Guid GetActorId()
        => HttpContext.Items["UserId"] is Guid id ? id : Guid.Empty;
}
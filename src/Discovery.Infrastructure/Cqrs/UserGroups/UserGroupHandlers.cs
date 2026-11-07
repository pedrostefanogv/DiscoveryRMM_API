using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.UserGroups.Commands;
using Discovery.Core.Cqrs.UserGroups.Queries;
using Discovery.Core.DTOs.Groups;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Services.Identity;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.UserGroups;

public sealed class CreateUserGroupCommandHandler(
    IUserGroupService service
) : IRequestHandler<CreateUserGroupCommand, Result<UserGroupDto>>
{
    public async Task<Result<UserGroupDto>> Handle(CreateUserGroupCommand cmd, CancellationToken ct)
    {
        var group = new UserGroup
        {
            Name = cmd.Name,
            Description = cmd.Description,
            IsActive = true
        };
        var created = await service.CreateAsync(group, ct);
        return Result<UserGroupDto>.Success(Map(created));
    }

    internal static UserGroupDto Map(UserGroup g) => new(
        g.Id, g.Name, g.Description, g.IsActive, g.CreatedAt, g.UpdatedAt);
}

public sealed class UpdateUserGroupCommandHandler(
    IUserGroupService service,
    IPermissionService permissions
) : IRequestHandler<UpdateUserGroupCommand, Result<UserGroupDto>>
{
    public async Task<Result<UserGroupDto>> Handle(UpdateUserGroupCommand cmd, CancellationToken ct)
    {
        var group = await service.GetByIdAsync(cmd.Id, ct);
        if (group is null)
            return Result<UserGroupDto>.Failure(Error.NotFound($"UserGroup {cmd.Id} not found"));

        if (cmd.Name is not null) group.Name = cmd.Name;
        if (cmd.Description is not null) group.Description = cmd.Description;

        var activeChanged = cmd.IsActive.HasValue && cmd.IsActive.Value != group.IsActive;
        if (cmd.IsActive.HasValue) group.IsActive = cmd.IsActive.Value;

        var updated = await service.UpdateAsync(group, ct);

        // Grupo inativo deixa de conceder permissões (filtro em
        // GetRolesWithPermissionsForUserAsync): sem invalidar, os membros manteriam as
        // permissões antigas pelo TTL do cache.
        if (activeChanged)
            await permissions.InvalidateAllCacheAsync();

        return Result<UserGroupDto>.Success(CreateUserGroupCommandHandler.Map(updated));
    }
}

public sealed class DeleteUserGroupCommandHandler(
    IUserGroupService service,
    IPermissionService permissions,
    Discovery.Infrastructure.Data.DiscoveryDbContext db
) : IRequestHandler<DeleteUserGroupCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteUserGroupCommand cmd, CancellationToken ct)
    {
        var deleted = await service.DeleteAsync(cmd.Id, ct);

        // O grupo deixa de existir: as atribuições de role dele não podem mais valer.
        if (deleted)
        {
            await permissions.InvalidateAllCacheAsync();

            // Overrides de consentimento do grupo não fazem mais sentido.
            await db.RemoteConsentPolicyOverrides
                .Where(item => item.SubjectType == Discovery.Core.Enums.RemoteConsentSubjectType.UserGroup
                    && item.SubjectId == cmd.Id)
                .ExecuteDeleteAsync(ct);
        }

        return deleted
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"UserGroup {cmd.Id} not found"));
    }
}

public sealed class ListUserGroupsQueryHandler(
    IUserGroupService service
) : IRequestHandler<ListUserGroupsQuery, Result<IReadOnlyList<UserGroupDto>>>
{
    public async Task<Result<IReadOnlyList<UserGroupDto>>> Handle(ListUserGroupsQuery q, CancellationToken ct)
    {
        var groups = await service.GetAllAsync(ct);
        return Result<IReadOnlyList<UserGroupDto>>.Success(
            groups.Select(CreateUserGroupCommandHandler.Map).ToList().AsReadOnly());
    }
}

public sealed class GetUserGroupByIdQueryHandler(
    IUserGroupService service
) : IRequestHandler<GetUserGroupByIdQuery, Result<UserGroupDto>>
{
    public async Task<Result<UserGroupDto>> Handle(GetUserGroupByIdQuery q, CancellationToken ct)
    {
        var group = await service.GetByIdAsync(q.Id, ct);
        return group is null
            ? Result<UserGroupDto>.Failure(Error.NotFound($"UserGroup {q.Id} not found"))
            : Result<UserGroupDto>.Success(CreateUserGroupCommandHandler.Map(group));
    }
}

// ── Membros do grupo ─────────────────────────────────────────────────────────

public sealed class ListGroupMembersQueryHandler(
    IUserGroupRepository groups,
    IUserRepository users
) : IRequestHandler<ListGroupMembersQuery, Result<IReadOnlyList<GroupMemberDto>>>
{
    public async Task<Result<IReadOnlyList<GroupMemberDto>>> Handle(ListGroupMembersQuery q, CancellationToken ct)
    {
        if (await groups.GetByIdAsync(q.GroupId) is null)
            return Result<IReadOnlyList<GroupMemberDto>>.Failure(Error.NotFound($"UserGroup {q.GroupId} not found"));

        var memberships = (await groups.GetMembershipsAsync(q.GroupId)).ToList();
        // Uma única query para todos os usuários (evita N+1 em grupos grandes).
        var userMap = (await users.GetByIdsAsync(memberships.Select(m => m.UserId)))
            .ToDictionary(u => u.Id);

        var items = memberships
            .Select(m =>
            {
                userMap.TryGetValue(m.UserId, out var user);
                return new GroupMemberDto
                {
                    AssignmentId = m.UserId.ToString(),
                    UserId = m.UserId,
                    Login = user?.Login,
                    Email = user?.Email,
                    FullName = user?.FullName,
                    IsActive = user?.IsActive,
                    AddedAt = m.JoinedAt
                };
            })
            .OrderBy(m => m.FullName ?? m.Login ?? m.UserId.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

        return Result<IReadOnlyList<GroupMemberDto>>.Success(items);
    }
}

public sealed class AddGroupMemberCommandHandler(
    IUserGroupRepository groups,
    IUserRepository users,
    IPermissionService permissions,
    IScopeContext scopeContext,
    ISiteRepository sites
) : IRequestHandler<AddGroupMemberCommand, Result<GroupMemberDto>>
{
    public async Task<Result<GroupMemberDto>> Handle(AddGroupMemberCommand cmd, CancellationToken ct)
    {
        var group = await groups.GetByIdAsync(cmd.GroupId);
        if (group is null)
            return Result<GroupMemberDto>.Failure(Error.NotFound($"UserGroup {cmd.GroupId} not found"));
        if (!group.IsActive)
            return Result<GroupMemberDto>.Failure(Error.Validation("GroupId", "Grupo inativo não aceita novos membros."));

        // ESCOPO: vincular alguém a um grupo concede todas as atribuições daquele grupo.
        // Sem esta guarda, um admin de cliente adicionava quem quisesse a um grupo com
        // papel Global. O grupo só é vinculável se TODAS as suas atribuições estiverem
        // dentro do escopo do ator.
        var scopeDenied = await GroupGrantGuard.CheckGroupAsync(
            groups, scopeContext, cmd.RequestedByUserId, cmd.GroupId, sites);
        if (scopeDenied is not null)
            return Result<GroupMemberDto>.Failure(scopeDenied);

        var user = await users.GetByIdAsync(cmd.UserId);
        if (user is null)
            return Result<GroupMemberDto>.Failure(Error.NotFound($"User {cmd.UserId} not found"));
        if (!user.IsActive)
            return Result<GroupMemberDto>.Failure(Error.Validation("UserId", "Usuário inativo não pode ser vinculado."));

        await groups.AddMemberAsync(cmd.GroupId, cmd.UserId);

        // As permissões efetivas do usuário mudaram — invalida o cache do PermissionService.
        await permissions.InvalidateUserCacheAsync(cmd.UserId);

        // Data real do vínculo: o repository é idempotente, então a membership pode já
        // existir (antes devolvíamos "agora" mesmo para um vínculo antigo).
        var membership = await groups.GetMembershipAsync(cmd.GroupId, cmd.UserId);

        return Result<GroupMemberDto>.Success(new GroupMemberDto
        {
            AssignmentId = user.Id.ToString(),
            UserId = user.Id,
            Login = user.Login,
            Email = user.Email,
            FullName = user.FullName,
            IsActive = user.IsActive,
            AddedAt = membership?.JoinedAt ?? DateTime.UtcNow
        });
    }
}

public sealed class RemoveGroupMemberCommandHandler(
    IUserGroupRepository groups,
    IPermissionService permissions,
    IScopeContext scopeContext,
    ISiteRepository sites
) : IRequestHandler<RemoveGroupMemberCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveGroupMemberCommand cmd, CancellationToken ct)
    {
        if (await groups.GetByIdAsync(cmd.GroupId) is null)
            return Result<VoidResult>.Failure(Error.NotFound($"UserGroup {cmd.GroupId} not found"));

        var scopeDenied = await GroupGrantGuard.CheckGroupAsync(
            groups, scopeContext, cmd.RequestedByUserId, cmd.GroupId, sites);
        if (scopeDenied is not null)
            return Result<VoidResult>.Failure(scopeDenied);

        try
        {
            await groups.RemoveMemberAsync(cmd.GroupId, cmd.UserId);
        }
        catch (InvalidOperationException ex)
        {
            return Result<VoidResult>.Failure(Error.Validation("UserId", ex.Message));
        }

        await permissions.InvalidateUserCacheAsync(cmd.UserId);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

// ── Roles do grupo ───────────────────────────────────────────────────────────

public sealed class ListGroupRolesQueryHandler(
    IUserGroupRepository groups,
    IRoleRepository roles
) : IRequestHandler<ListGroupRolesQuery, Result<IReadOnlyList<GroupRoleAssignmentDto>>>
{
    public async Task<Result<IReadOnlyList<GroupRoleAssignmentDto>>> Handle(ListGroupRolesQuery q, CancellationToken ct)
    {
        if (await groups.GetByIdAsync(q.GroupId) is null)
            return Result<IReadOnlyList<GroupRoleAssignmentDto>>.Failure(Error.NotFound($"UserGroup {q.GroupId} not found"));

        var assignments = (await groups.GetRolesForGroupAsync(q.GroupId)).ToList();
        var roleMap = (await roles.GetByIdsAsync(assignments.Select(a => a.RoleId)))
            .ToDictionary(r => r.Id);

        var items = assignments
            .Select(a => new GroupRoleAssignmentDto
            {
                AssignmentId = a.Id,
                RoleId = a.RoleId,
                RoleName = roleMap.TryGetValue(a.RoleId, out var role) ? role.Name : string.Empty,
                ScopeLevel = a.ScopeLevel,
                ScopeId = a.ScopeId,
                ScopeName = null
            })
            .ToList()
            .AsReadOnly();

        return Result<IReadOnlyList<GroupRoleAssignmentDto>>.Success(items);
    }
}

public sealed class AssignGroupRoleCommandHandler(
    IUserGroupRepository groups,
    IRoleRepository roles,
    IPermissionService permissions,
    IScopeContext scopeContext,
    IClientRepository clients,
    ISiteRepository sites
) : IRequestHandler<AssignGroupRoleCommand, Result<GroupRoleAssignmentDto>>
{
    public async Task<Result<GroupRoleAssignmentDto>> Handle(AssignGroupRoleCommand cmd, CancellationToken ct)
    {
        if (await groups.GetByIdAsync(cmd.GroupId) is null)
            return Result<GroupRoleAssignmentDto>.Failure(Error.NotFound($"UserGroup {cmd.GroupId} not found"));

        var role = await roles.GetByIdAsync(cmd.RoleId);
        if (role is null)
            return Result<GroupRoleAssignmentDto>.Failure(Error.NotFound($"Role {cmd.RoleId} not found"));

        if (cmd.ScopeLevel != ScopeLevel.Global && cmd.ScopeId is null)
            return Result<GroupRoleAssignmentDto>.Failure(
                Error.Validation("ScopeId", "ScopeId é obrigatório quando o escopo é Client ou Site."));

        // ESCOPO DO CONCEDENTE — o vetor de escalação entre tenants (achado P0-2).
        // O payload { roleId: <admin>, scopeLevel: "Global" } era aceito por qualquer
        // portador de Users:Edit. A regra da v1 é por escopo: só se concede o que está
        // contido no próprio escopo.
        Guid? parentClientId = null;
        if (cmd.ScopeLevel == ScopeLevel.Site && cmd.ScopeId.HasValue && cmd.ScopeId.Value != Guid.Empty)
        {
            var site = await sites.GetByIdAsync(cmd.ScopeId.Value);
            if (site is null)
                return Result<GroupRoleAssignmentDto>.Failure(
                    Error.Validation("ScopeId", "Site informado não existe."));
            parentClientId = site.ClientId;
        }
        else if (cmd.ScopeLevel == ScopeLevel.Client && cmd.ScopeId.HasValue && cmd.ScopeId.Value != Guid.Empty)
        {
            // Antes o ScopeId era gravado sem qualquer validação: um GUID inexistente
            // criava atribuição "válida" que não concedia nada e poluía a auditoria.
            if (await clients.GetByIdAsync(cmd.ScopeId.Value) is null)
                return Result<GroupRoleAssignmentDto>.Failure(
                    Error.Validation("ScopeId", "Cliente informado não existe."));
        }

        if (cmd.RequestedByUserId != Guid.Empty)
        {
            scopeContext.SetUserId(cmd.RequestedByUserId);
            var granterAccess = await scopeContext.GetAccessAsync(ResourceType.Users, ActionType.Edit);

            if (!RoleGrantScopePolicy.CanGrant(granterAccess, cmd.ScopeLevel, cmd.ScopeId, parentClientId))
            {
                return Result<GroupRoleAssignmentDto>.Failure(
                    Error.Forbidden("Você não pode conceder acesso fora do seu escopo."));
            }
        }

        var existing = (await groups.GetRolesForGroupAsync(cmd.GroupId)).ToList();
        if (existing.Any(a => a.RoleId == cmd.RoleId && a.ScopeLevel == cmd.ScopeLevel && a.ScopeId == cmd.ScopeId))
            return Result<GroupRoleAssignmentDto>.Failure(
                Error.Conflict("Esta role já está vinculada ao grupo neste escopo."));

        var assignment = new UserGroupRole
        {
            GroupId = cmd.GroupId,
            RoleId = cmd.RoleId,
            ScopeLevel = cmd.ScopeLevel,
            ScopeId = cmd.ScopeId
        };

        try
        {
            await groups.AssignRoleAsync(assignment);
        }
        catch (DbUpdateException)
        {
            // Índice único ux_user_group_roles_scope (M196): outra requisição criou a mesma
            // atribuição entre a checagem e o insert.
            return Result<GroupRoleAssignmentDto>.Failure(
                Error.Conflict("Esta role já está vinculada ao grupo neste escopo."));
        }

        await UserGroupPermissionInvalidation.InvalidateMembersAsync(groups, permissions, cmd.GroupId);

        return Result<GroupRoleAssignmentDto>.Success(new GroupRoleAssignmentDto
        {
            AssignmentId = assignment.Id,
            RoleId = assignment.RoleId,
            RoleName = role.Name,
            ScopeLevel = assignment.ScopeLevel,
            ScopeId = assignment.ScopeId,
            ScopeName = null
        });
    }
}

public sealed class RemoveGroupRoleAssignmentCommandHandler(
    IUserGroupRepository groups,
    IPermissionService permissions,
    IScopeContext scopeContext,
    ISiteRepository sites
) : IRequestHandler<RemoveGroupRoleAssignmentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveGroupRoleAssignmentCommand cmd, CancellationToken ct)
    {
        if (await groups.GetByIdAsync(cmd.GroupId) is null)
            return Result<VoidResult>.Failure(Error.NotFound($"UserGroup {cmd.GroupId} not found"));

        // RemoveRoleAssignmentAsync filtra apenas por assignmentId: validar o vínculo com o
        // grupo impede que um assignmentId de outro grupo seja removido por engano/abuso.
        var assignment = (await groups.GetRolesForGroupAsync(cmd.GroupId))
            .FirstOrDefault(a => a.Id == cmd.AssignmentId);

        if (assignment is null)
            return Result<VoidResult>.Failure(Error.NotFound("Atribuição de role não encontrada neste grupo."));

        // Remover também é alteração de privilégio: um admin de cliente não pode
        // desmontar o acesso de outro tenant (nem se livrar de uma restrição alheia).
        if (cmd.RequestedByUserId != Guid.Empty)
        {
            Guid? parentClientId = null;
            if (assignment.ScopeLevel == ScopeLevel.Site && assignment.ScopeId.HasValue && assignment.ScopeId.Value != Guid.Empty)
            {
                var site = await sites.GetByIdAsync(assignment.ScopeId.Value);
                parentClientId = site?.ClientId;
            }

            scopeContext.SetUserId(cmd.RequestedByUserId);
            var granterAccess = await scopeContext.GetAccessAsync(ResourceType.Users, ActionType.Edit);

            if (!RoleGrantScopePolicy.CanGrant(granterAccess, assignment.ScopeLevel, assignment.ScopeId, parentClientId))
            {
                return Result<VoidResult>.Failure(
                    Error.Forbidden("Você não pode remover acesso fora do seu escopo."));
            }
        }

        await groups.RemoveRoleAssignmentAsync(cmd.AssignmentId);
        await UserGroupPermissionInvalidation.InvalidateMembersAsync(groups, permissions, cmd.GroupId);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

/// <summary>
/// Guarda de "quem pode mexer neste grupo". Um grupo é um pacote de atribuições
/// (papel + escopo); se QUALQUER atribuição do grupo estiver fora do escopo do
/// ator, ele não pode vincular/desvincular membros — senão contornaria a guarda
/// de concessão passando pelo membro em vez de pelo papel.
/// </summary>
internal static class GroupGrantGuard
{
    /// <summary>
    /// Retorna null quando permitido, ou o erro a devolver. Ator sem id (chamadas
    /// internas/seed) passa direto, preservando compatibilidade.
    /// </summary>
    /// <param name="sites">
    /// Necessário para resolver o CLIENTE pai de uma atribuição Site. Sem ele, uma
    /// concessão legítima no nível de Client não autoriza gerenciar um grupo cuja
    /// atribuição é Site-scoped — a policy só consegue aplicar a herança
    /// Client→Site se souber qual é o cliente do site.
    /// </param>
    internal static async Task<Error?> CheckGroupAsync(
        IUserGroupRepository groups,
        IScopeContext scopeContext,
        Guid requestedByUserId,
        Guid groupId,
        ISiteRepository? sites = null)
    {
        if (requestedByUserId == Guid.Empty)
            return null;

        scopeContext.SetUserId(requestedByUserId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Users, ActionType.Edit);

        if (access.HasGlobalAccess)
            return null;

        foreach (var assignment in await groups.GetRolesForGroupAsync(groupId))
        {
            Guid? parentClientId = null;
            if (sites is not null
                && assignment.ScopeLevel == ScopeLevel.Site
                && assignment.ScopeId.HasValue
                && assignment.ScopeId.Value != Guid.Empty)
            {
                var site = await sites.GetByIdAsync(assignment.ScopeId.Value);
                parentClientId = site?.ClientId;
            }

            if (!RoleGrantScopePolicy.CanGrant(access, assignment.ScopeLevel, assignment.ScopeId, parentClientId))
                return Error.Forbidden("Este grupo concede acesso fora do seu escopo.");
        }

        return null;
    }
}

internal static class UserGroupPermissionInvalidation
{
    /// <summary>
    /// Invalida o cache de permissões de todos os membros do grupo. Sem isso, alterações de
    /// role/escopo só passariam a valer após o TTL do cache (5 min) ou restart da API.
    /// </summary>
    internal static async Task InvalidateMembersAsync(
        IUserGroupRepository groups,
        IPermissionService permissions,
        Guid groupId)
    {
        var memberIds = (await groups.GetMemberIdsAsync(groupId)).ToList();
        if (memberIds.Count == 0)
            return;

        // Antes: await sequencial por membro. Num grupo com milhares de membros isso
        // eram milhares de DELs do Redis dentro de UM request, somando latência ao
        // tempo de resposta da operação administrativa. A invalidação é idempotente
        // e independente por usuário, então pode ser paralela.
        //
        // Concorrência limitada: paralelizar tudo de uma vez num grupo grande
        // saturaria o pool de conexões do Redis e derrubaria requests concorrentes.
        const int maxParallelism = 16;
        using var gate = new SemaphoreSlim(maxParallelism);

        var tasks = memberIds.Select(async userId =>
        {
            await gate.WaitAsync();
            try
            {
                await permissions.InvalidateUserCacheAsync(userId);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }
}

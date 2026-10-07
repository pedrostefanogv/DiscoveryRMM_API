using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.UserGroups.Commands;
using Discovery.Core.Cqrs.UserGroups.Queries;
using Discovery.Core.DTOs.Groups;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
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
    IPermissionService permissions
) : IRequestHandler<DeleteUserGroupCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteUserGroupCommand cmd, CancellationToken ct)
    {
        var deleted = await service.DeleteAsync(cmd.Id, ct);

        // O grupo deixa de existir: as atribuições de role dele não podem mais valer.
        if (deleted)
            await permissions.InvalidateAllCacheAsync();

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
    IPermissionService permissions
) : IRequestHandler<AddGroupMemberCommand, Result<GroupMemberDto>>
{
    public async Task<Result<GroupMemberDto>> Handle(AddGroupMemberCommand cmd, CancellationToken ct)
    {
        var group = await groups.GetByIdAsync(cmd.GroupId);
        if (group is null)
            return Result<GroupMemberDto>.Failure(Error.NotFound($"UserGroup {cmd.GroupId} not found"));
        if (!group.IsActive)
            return Result<GroupMemberDto>.Failure(Error.Validation("GroupId", "Grupo inativo não aceita novos membros."));

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
    IPermissionService permissions
) : IRequestHandler<RemoveGroupMemberCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveGroupMemberCommand cmd, CancellationToken ct)
    {
        if (await groups.GetByIdAsync(cmd.GroupId) is null)
            return Result<VoidResult>.Failure(Error.NotFound($"UserGroup {cmd.GroupId} not found"));

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
    IPermissionService permissions
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
    IPermissionService permissions
) : IRequestHandler<RemoveGroupRoleAssignmentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveGroupRoleAssignmentCommand cmd, CancellationToken ct)
    {
        if (await groups.GetByIdAsync(cmd.GroupId) is null)
            return Result<VoidResult>.Failure(Error.NotFound($"UserGroup {cmd.GroupId} not found"));

        // RemoveRoleAssignmentAsync filtra apenas por assignmentId: validar o vínculo com o
        // grupo impede que um assignmentId de outro grupo seja removido por engano/abuso.
        var belongsToGroup = (await groups.GetRolesForGroupAsync(cmd.GroupId))
            .Any(a => a.Id == cmd.AssignmentId);

        if (!belongsToGroup)
            return Result<VoidResult>.Failure(Error.NotFound("Atribuição de role não encontrada neste grupo."));

        await groups.RemoveRoleAssignmentAsync(cmd.AssignmentId);
        await UserGroupPermissionInvalidation.InvalidateMembersAsync(groups, permissions, cmd.GroupId);

        return Result<VoidResult>.Success(VoidResult.Value);
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
        foreach (var userId in await groups.GetMemberIdsAsync(groupId))
            await permissions.InvalidateUserCacheAsync(userId);
    }
}

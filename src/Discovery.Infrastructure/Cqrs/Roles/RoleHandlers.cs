using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Roles.Commands;
using Discovery.Core.Cqrs.Roles.Queries;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Roles;

internal static class RoleMapping
{
    internal static RoleDto ToDto(Role r) => new(
        r.Id, r.Name, r.Description, r.IsSystem, r.MfaRequirement, r.IsActive, r.CreatedAt, r.UpdatedAt);

    /// <summary>
    /// O catálogo de permissões não tem coluna de código: o "código" é a composição
    /// Recurso.Ação (ex.: "Users.View"), exatamente o formato usado pelos gates do console.
    /// </summary>
    internal static PermissionDto ToDto(Permission p)
    {
        var code = $"{p.ResourceType}.{p.ActionType}";
        return new PermissionDto(p.Id, code, p.Description ?? code, p.Description,
            p.ResourceType.ToString(), p.ActionType.ToString());
    }
}

public sealed class CreateRoleCommandHandler(
    IRoleService service
) : IRequestHandler<CreateRoleCommand, Result<RoleDto>>
{
    public async Task<Result<RoleDto>> Handle(CreateRoleCommand cmd, CancellationToken ct)
    {
        var role = new Role
        {
            Name = cmd.Name,
            Description = cmd.Description,
            MfaRequirement = cmd.MfaRequirement,
            IsActive = true
        };
        var created = await service.CreateAsync(role, ct);
        return Result<RoleDto>.Success(RoleMapping.ToDto(created));
    }
}

public sealed class UpdateRoleCommandHandler(
    IRoleService service,
    IPermissionService permissions
) : IRequestHandler<UpdateRoleCommand, Result<RoleDto>>
{
    public async Task<Result<RoleDto>> Handle(UpdateRoleCommand cmd, CancellationToken ct)
    {
        var role = await service.GetByIdAsync(cmd.Id, ct);
        if (role is null)
            return Result<RoleDto>.Failure(Error.NotFound($"Role {cmd.Id} not found"));
        if (role.IsSystem)
            return Result<RoleDto>.Failure(Error.Validation("Id", "System roles cannot be modified"));

        if (cmd.Name is not null) role.Name = cmd.Name;
        if (cmd.Description is not null) role.Description = cmd.Description;

        // Política de MFA por role; liberada também para roles de sistema seria ideal,
        // mas a regra atual bloqueia qualquer edição de role de sistema.
        if (cmd.MfaRequirement.HasValue) role.MfaRequirement = cmd.MfaRequirement.Value;

        var permissionsChanged = false;
        if (cmd.IsActive.HasValue && cmd.IsActive.Value != role.IsActive)
        {
            role.IsActive = cmd.IsActive.Value;
            // Role inativa deixa de conceder permissões (filtro em GetRolesWithPermissionsForUserAsync).
            permissionsChanged = true;
        }

        var updated = await service.UpdateAsync(role, ct);

        if (permissionsChanged)
            await permissions.InvalidateAllCacheAsync();

        return Result<RoleDto>.Success(RoleMapping.ToDto(updated));
    }
}

public sealed class DeleteRoleCommandHandler(
    IRoleService service,
    IPermissionService permissions
) : IRequestHandler<DeleteRoleCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteRoleCommand cmd, CancellationToken ct)
    {
        var role = await service.GetByIdAsync(cmd.Id, ct);
        if (role is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Role {cmd.Id} not found"));
        if (role.IsSystem)
            return Result<VoidResult>.Failure(Error.Validation("Id", "System roles cannot be deleted"));

        var deleted = await service.DeleteAsync(cmd.Id, ct);
        if (deleted)
            await permissions.InvalidateAllCacheAsync();

        return deleted
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Role {cmd.Id} not found"));
    }
}

public sealed class ListRolesQueryHandler(
    IRoleService service
) : IRequestHandler<ListRolesQuery, Result<IReadOnlyList<RoleDto>>>
{
    public async Task<Result<IReadOnlyList<RoleDto>>> Handle(ListRolesQuery q, CancellationToken ct)
    {
        var roles = await service.GetAllAsync(ct);
        return Result<IReadOnlyList<RoleDto>>.Success(
            roles.Select(RoleMapping.ToDto).ToList().AsReadOnly());
    }
}

public sealed class GetRoleByIdQueryHandler(
    IRoleService service
) : IRequestHandler<GetRoleByIdQuery, Result<RoleDto>>
{
    public async Task<Result<RoleDto>> Handle(GetRoleByIdQuery q, CancellationToken ct)
    {
        var role = await service.GetByIdAsync(q.Id, ct);
        return role is null
            ? Result<RoleDto>.Failure(Error.NotFound($"Role {q.Id} not found"))
            : Result<RoleDto>.Success(RoleMapping.ToDto(role));
    }
}

// ── Permissões de role ───────────────────────────────────────────────────────

public sealed class ListRolePermissionsQueryHandler(
    IRoleService service
) : IRequestHandler<ListRolePermissionsQuery, Result<IReadOnlyList<PermissionDto>>>
{
    public async Task<Result<IReadOnlyList<PermissionDto>>> Handle(ListRolePermissionsQuery q, CancellationToken ct)
    {
        if (await service.GetByIdAsync(q.RoleId, ct) is null)
            return Result<IReadOnlyList<PermissionDto>>.Failure(Error.NotFound($"Role {q.RoleId} not found"));

        var permissions = await service.GetPermissionsAsync(q.RoleId, ct);
        return Result<IReadOnlyList<PermissionDto>>.Success(
            permissions.Select(RoleMapping.ToDto).ToList().AsReadOnly());
    }
}

public sealed class ListPermissionsCatalogQueryHandler(
    IRoleService service
) : IRequestHandler<ListPermissionsCatalogQuery, Result<IReadOnlyList<PermissionDto>>>
{
    public async Task<Result<IReadOnlyList<PermissionDto>>> Handle(ListPermissionsCatalogQuery q, CancellationToken ct)
    {
        var permissions = await service.GetAllPermissionsAsync(ct);
        return Result<IReadOnlyList<PermissionDto>>.Success(
            permissions.Select(RoleMapping.ToDto).OrderBy(p => p.Code, StringComparer.Ordinal).ToList().AsReadOnly());
    }
}

public sealed class AddRolePermissionCommandHandler(
    IRoleService service,
    IPermissionService permissions
) : IRequestHandler<AddRolePermissionCommand, Result<PermissionDto>>
{
    public async Task<Result<PermissionDto>> Handle(AddRolePermissionCommand cmd, CancellationToken ct)
    {
        if (await service.GetByIdAsync(cmd.RoleId, ct) is null)
            return Result<PermissionDto>.Failure(Error.NotFound($"Role {cmd.RoleId} not found"));

        var catalog = await service.GetAllPermissionsAsync(ct);
        var permission = catalog.FirstOrDefault(p => p.Id == cmd.PermissionId);

        if (permission is null)
            return Result<PermissionDto>.Failure(Error.NotFound($"Permission {cmd.PermissionId} not found"));

        await service.AddPermissionAsync(cmd.RoleId, cmd.PermissionId, ct);
        await permissions.InvalidateAllCacheAsync();

        return Result<PermissionDto>.Success(RoleMapping.ToDto(permission));
    }
}

public sealed class RemoveRolePermissionCommandHandler(
    IRoleService service,
    IPermissionService permissions
) : IRequestHandler<RemoveRolePermissionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveRolePermissionCommand cmd, CancellationToken ct)
    {
        if (await service.GetByIdAsync(cmd.RoleId, ct) is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Role {cmd.RoleId} not found"));

        await service.RemovePermissionAsync(cmd.RoleId, cmd.PermissionId, ct);
        await permissions.InvalidateAllCacheAsync();

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Roles.Commands;
using Discovery.Core.Cqrs.Roles.Queries;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces.Identity;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Roles;

public sealed class CreateRoleCommandHandler(
    IRoleService service
) : IRequestHandler<CreateRoleCommand, Result<RoleDto>>
{
    public async Task<Result<RoleDto>> Handle(CreateRoleCommand cmd, CancellationToken ct)
    {
        var role = new Role
        {
            Name = cmd.Name,
            Description = cmd.Description
        };
        var created = await service.CreateAsync(role, ct);
        return Result<RoleDto>.Success(Map(created));
    }

    internal static RoleDto Map(Role r) => new(r.Id, r.Name, r.Description, r.IsSystem, r.CreatedAt);
}

public sealed class UpdateRoleCommandHandler(
    IRoleService service
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

        var updated = await service.UpdateAsync(role, ct);
        return Result<RoleDto>.Success(CreateRoleCommandHandler.Map(updated));
    }
}

public sealed class DeleteRoleCommandHandler(
    IRoleService service
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
            roles.Select(CreateRoleCommandHandler.Map).ToList().AsReadOnly());
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
            : Result<RoleDto>.Success(CreateRoleCommandHandler.Map(role));
    }
}

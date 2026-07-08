using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Roles.Commands;

public sealed record CreateRoleCommand(
    string Name, string? Description
) : ICommand<Result<RoleDto>>;

public sealed record UpdateRoleCommand(
    Guid Id, string? Name, string? Description
) : ICommand<Result<RoleDto>>;

public sealed record DeleteRoleCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record RoleDto(
    Guid Id, string Name, string? Description,
    bool IsSystem, DateTime CreatedAt
);

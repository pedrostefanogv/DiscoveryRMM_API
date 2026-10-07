using Discovery.Core.Cqrs;
using Discovery.Core.Enums.Identity;

namespace Discovery.Core.Cqrs.Roles.Commands;

public sealed record CreateRoleCommand(
    string Name, string? Description,
    RoleMfaRequirement MfaRequirement = RoleMfaRequirement.None
) : ICommand<Result<RoleDto>>;

public sealed record UpdateRoleCommand(
    Guid Id, string? Name, string? Description,
    RoleMfaRequirement? MfaRequirement = null,
    bool? IsActive = null
) : ICommand<Result<RoleDto>>;

public sealed record DeleteRoleCommand(Guid Id) : ICommand<Result<VoidResult>>;

/// <summary>Concede uma permissão a uma role.</summary>
public sealed record AddRolePermissionCommand(
    Guid RoleId, Guid PermissionId
) : ICommand<Result<PermissionDto>>;

/// <summary>Revoga uma permissão de uma role.</summary>
public sealed record RemoveRolePermissionCommand(
    Guid RoleId, Guid PermissionId
) : ICommand<Result<VoidResult>>;

public sealed record RoleDto(
    Guid Id, string Name, string? Description,
    bool IsSystem, RoleMfaRequirement MfaRequirement, bool IsActive,
    DateTime CreatedAt, DateTime UpdatedAt
);

/// <summary>
/// Permissão exposta ao console web. <c>Code</c> é o formato usado pelos gates do frontend
/// (ex.: "Users.View") e <c>Name</c> reaproveita a descrição semeada na migration M069.
/// </summary>
public sealed record PermissionDto(
    Guid Id, string Code, string Name, string? Description,
    string? ResourceType, string? ActionType
);

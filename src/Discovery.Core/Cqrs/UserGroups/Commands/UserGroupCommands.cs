using Discovery.Core.Cqrs;
using Discovery.Core.DTOs.Groups;
using Discovery.Core.Enums.Identity;

namespace Discovery.Core.Cqrs.UserGroups.Commands;

public sealed record CreateUserGroupCommand(
    string Name, string? Description
) : ICommand<Result<UserGroupDto>>;

public sealed record UpdateUserGroupCommand(
    Guid Id, string? Name, string? Description, bool? IsActive
) : ICommand<Result<UserGroupDto>>;

public sealed record DeleteUserGroupCommand(Guid Id) : ICommand<Result<VoidResult>>;

/// <summary>Vincula um usuário ao grupo.</summary>
public sealed record AddGroupMemberCommand(
    Guid GroupId, Guid UserId
) : ICommand<Result<GroupMemberDto>>;

/// <summary>Remove o vínculo do usuário com o grupo.</summary>
public sealed record RemoveGroupMemberCommand(
    Guid GroupId, Guid UserId
) : ICommand<Result<VoidResult>>;

/// <summary>Atribui uma role ao grupo, em um nível de escopo (Global/Client/Site).</summary>
public sealed record AssignGroupRoleCommand(
    Guid GroupId, Guid RoleId, ScopeLevel ScopeLevel, Guid? ScopeId
) : ICommand<Result<GroupRoleAssignmentDto>>;

/// <summary>Remove uma atribuição de role do grupo.</summary>
public sealed record RemoveGroupRoleAssignmentCommand(
    Guid GroupId, Guid AssignmentId
) : ICommand<Result<VoidResult>>;

public sealed record UserGroupDto(
    Guid Id, string Name, string? Description,
    bool IsActive, DateTime CreatedAt, DateTime UpdatedAt
);

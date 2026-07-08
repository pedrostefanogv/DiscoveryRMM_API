using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.UserGroups.Commands;

public sealed record CreateUserGroupCommand(
    string Name, string? Description
) : ICommand<Result<UserGroupDto>>;

public sealed record UpdateUserGroupCommand(
    Guid Id, string? Name, string? Description, bool? IsActive
) : ICommand<Result<UserGroupDto>>;

public sealed record DeleteUserGroupCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record UserGroupDto(
    Guid Id, string Name, string? Description,
    bool IsActive, DateTime CreatedAt, DateTime UpdatedAt
);

using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.UserGroups.Commands;

namespace Discovery.Core.Cqrs.UserGroups.Queries;

public sealed record ListUserGroupsQuery : IQuery<Result<IReadOnlyList<UserGroupDto>>>;
public sealed record GetUserGroupByIdQuery(Guid Id) : IQuery<Result<UserGroupDto>>;

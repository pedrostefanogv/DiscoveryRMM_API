using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Roles.Commands;

namespace Discovery.Core.Cqrs.Roles.Queries;

public sealed record ListRolesQuery : IQuery<Result<IReadOnlyList<RoleDto>>>;
public sealed record GetRoleByIdQuery(Guid Id) : IQuery<Result<RoleDto>>;

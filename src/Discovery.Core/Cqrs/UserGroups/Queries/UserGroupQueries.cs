using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.UserGroups.Commands;
using Discovery.Core.DTOs.Groups;

namespace Discovery.Core.Cqrs.UserGroups.Queries;

public sealed record ListUserGroupsQuery : IQuery<Result<IReadOnlyList<UserGroupDto>>>;
public sealed record GetUserGroupByIdQuery(Guid Id) : IQuery<Result<UserGroupDto>>;

/// <summary>Membros do grupo com dados de exibição (nome/login/e-mail).</summary>
public sealed record ListGroupMembersQuery(Guid GroupId) : IQuery<Result<IReadOnlyList<GroupMemberDto>>>;

/// <summary>Roles atribuídas ao grupo, com nome da role e do escopo.</summary>
public sealed record ListGroupRolesQuery(Guid GroupId) : IQuery<Result<IReadOnlyList<GroupRoleAssignmentDto>>>;

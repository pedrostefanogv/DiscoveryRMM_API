using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Roles.Commands;

namespace Discovery.Core.Cqrs.Roles.Queries;

public sealed record ListRolesQuery : IQuery<Result<IReadOnlyList<RoleDto>>>;
public sealed record GetRoleByIdQuery(Guid Id) : IQuery<Result<RoleDto>>;

/// <summary>Permissões concedidas a uma role.</summary>
public sealed record ListRolePermissionsQuery(Guid RoleId) : IQuery<Result<IReadOnlyList<PermissionDto>>>;

/// <summary>Catálogo completo de permissões (para a tela de roles).</summary>
public sealed record ListPermissionsCatalogQuery : IQuery<Result<IReadOnlyList<PermissionDto>>>;

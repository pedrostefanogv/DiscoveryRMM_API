using Discovery.Core.Entities.Identity;

namespace Discovery.Core.DTOs.Identity;

public sealed record RoleAssignmentWithPermissions(
    UserGroupRole Assignment,
    IReadOnlyList<Permission> Permissions);

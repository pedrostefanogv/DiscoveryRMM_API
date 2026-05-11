namespace Discovery.Core.DTOs.Identity;

public sealed record CachedPermissionEntry(
    Guid RoleId,
    string ScopeLevel,
    Guid? ScopeId,
    string ResourceType,
    string ActionType);

using System.Text.Json;
using Discovery.Core.DTOs.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;

namespace Discovery.Infrastructure.Services;

/// <inheritdoc />
public class PermissionService : IPermissionService
{
    private readonly IUserGroupRepository _groups;
    private readonly IRedisService _redis;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private const string CachePrefix = "perm:user:";
    private const int CacheTtl = 300;

    public PermissionService(IUserGroupRepository groups, IRedisService redis)
    {
        _groups = groups;
        _redis = redis;
    }

    public async Task<bool> HasPermissionAsync(
        Guid userId,
        ResourceType resource,
        ActionType action,
        ScopeLevel scopeLevel = ScopeLevel.Global,
        Guid? scopeId = null,
        Guid? parentScopeId = null)
    {
        var assignments = await GetUserRolesAsync(userId);

        foreach (var assignment in assignments)
        {
            if (!IsScopeMatch(assignment.Assignment.ScopeLevel, assignment.Assignment.ScopeId, scopeLevel, scopeId, parentScopeId))
                continue;

            if (assignment.Permissions.Any(p => p.ResourceType == resource && p.ActionType == action))
                return true;
        }

        return false;
    }

    public async Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
    {
        var assignments = await GetUserRolesAsync(userId);

        var hasGlobal = false;
        var clientIds = new List<Guid>();
        var siteIds = new List<Guid>();

        foreach (var assignment in assignments)
        {
            if (!assignment.Permissions.Any(p => p.ResourceType == resource && p.ActionType == action))
                continue;

            switch (assignment.Assignment.ScopeLevel)
            {
                case ScopeLevel.Global:
                    hasGlobal = true;
                    break;
                case ScopeLevel.Client when assignment.Assignment.ScopeId.HasValue:
                    clientIds.Add(assignment.Assignment.ScopeId.Value);
                    break;
                case ScopeLevel.Site when assignment.Assignment.ScopeId.HasValue:
                    siteIds.Add(assignment.Assignment.ScopeId.Value);
                    break;
            }
        }

        return new UserScopeAccess
        {
            HasGlobalAccess = hasGlobal,
            AllowedClientIds = clientIds.Distinct().ToList(),
            AllowedSiteIds = siteIds.Distinct().ToList()
        };
    }

    private readonly Dictionary<Guid, IReadOnlyList<RoleAssignmentWithPermissions>> _userRoleCache = new();

    private async Task<IReadOnlyList<RoleAssignmentWithPermissions>> GetUserRolesAsync(Guid userId)
    {
        if (_userRoleCache.TryGetValue(userId, out var roles))
            return roles;

        roles = await TryLoadFromRedisAsync(userId);
        if (roles is not null)
        {
            _userRoleCache[userId] = roles;
            return roles;
        }

        roles = (await _groups.GetRolesWithPermissionsForUserAsync(userId)).ToList();
        _userRoleCache[userId] = roles;

        _ = SaveToRedisAsync(userId, roles);

        return roles;
    }

    private async Task<IReadOnlyList<RoleAssignmentWithPermissions>?> TryLoadFromRedisAsync(Guid userId)
    {
        try
        {
            var cached = await _redis.GetAsync($"{CachePrefix}{userId:N}");
            if (cached is null) return null;

            var flat = JsonSerializer.Deserialize<List<CachedPermissionEntry>>(cached, _jsonOptions);
            if (flat is null || flat.Count == 0) return null;

            var grouped = flat
                .GroupBy(e => new { e.RoleId, e.ScopeLevel, e.ScopeId })
                .Select(g =>
                {
                    var first = g.First();
                    var sl = Enum.TryParse<ScopeLevel>(first.ScopeLevel, out var parsed) ? parsed : ScopeLevel.Global;
                    return new RoleAssignmentWithPermissions(
                        new Core.Entities.Identity.UserGroupRole
                        {
                            RoleId = first.RoleId,
                            ScopeLevel = sl,
                            ScopeId = first.ScopeId
                        },
                        g.Select(e => new Core.Entities.Identity.Permission
                        {
                            ResourceType = Enum.TryParse<ResourceType>(e.ResourceType, out var r) ? r : default,
                            ActionType = Enum.TryParse<ActionType>(e.ActionType, out var a) ? a : default
                        }).ToList());
                })
                .ToList();

            return grouped;
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveToRedisAsync(Guid userId, IReadOnlyList<RoleAssignmentWithPermissions> roles)
    {
        try
        {
            var flat = roles.SelectMany(r =>
                r.Permissions.Select(p => new CachedPermissionEntry(
                    r.Assignment.RoleId,
                    r.Assignment.ScopeLevel.ToString(),
                    r.Assignment.ScopeId,
                    p.ResourceType.ToString(),
                    p.ActionType.ToString())))
                .Distinct()
                .ToList();

            var json = JsonSerializer.Serialize(flat, _jsonOptions);
            await _redis.SetAsync($"{CachePrefix}{userId:N}", json, CacheTtl);
        }
        catch
        {
            // Redis indisponivel — cache salta silenciosamente
        }
    }

    private bool IsScopeMatch(
        ScopeLevel assignmentScope,
        Guid? assignmentScopeId,
        ScopeLevel requestedScope,
        Guid? requestedScopeId,
        Guid? parentScopeId)
    {
        if (assignmentScope == ScopeLevel.Global)
            return true;

        if (assignmentScope == ScopeLevel.Client && assignmentScopeId.HasValue)
        {
            if (requestedScope == ScopeLevel.Client && requestedScopeId == assignmentScopeId)
                return true;
            if (requestedScope == ScopeLevel.Site && parentScopeId == assignmentScopeId)
                return true;
        }

        if (assignmentScope == ScopeLevel.Site && assignmentScopeId.HasValue)
        {
            if (requestedScope == ScopeLevel.Site && requestedScopeId == assignmentScopeId)
                return true;
        }

        return false;
    }
}

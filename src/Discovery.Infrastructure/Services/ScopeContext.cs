using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementacao padrao de IScopeContext com cache intra-request por (resourceType, actionType).
/// </summary>
public class ScopeContext : IScopeContext
{
    private readonly IPermissionService _permissionService;
    private Guid? _userId;

    private readonly Dictionary<(ResourceType, ActionType), UserScopeAccess> _cache = new();

    public Guid? ResolvedClientId { get; set; }
    public Guid? ResolvedSiteId { get; set; }

    public ScopeContext(IPermissionService permissionService)
    {
        _permissionService = permissionService;
    }

    public void SetUserId(Guid userId)
    {
        _userId = userId;
    }

    public async Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action)
    {
        var key = (resource, action);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        if (_userId is null)
            return new UserScopeAccess { HasGlobalAccess = false };

        var access = await _permissionService.GetScopeAccessAsync(_userId.Value, resource, action);
        _cache[key] = access;
        return access;
    }

    public async Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action)
    {
        var access = await GetAccessAsync(resource, action);
        return access.HasGlobalAccess;
    }
}

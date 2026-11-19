using System.Reflection;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;

namespace Discovery.Tests;

internal class F02NullProxy<T> : DispatchProxy where T : class
{
    private Func<MethodInfo, object?[]?, object?>? _handler;

    public static T Create(Func<MethodInfo, object?[]?, object?>? handler = null)
    {
        var proxy = Create<T, F02NullProxy<T>>();
        ((F02NullProxy<T>)(object)proxy)._handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null) return null;
        if (_handler is not null)
        {
            var handled = _handler(targetMethod, args);
            if (handled is not null) return handled;
        }

        var ret = targetMethod.ReturnType;
        if (ret == typeof(void)) return null;
        if (ret == typeof(Task)) return Task.CompletedTask;
        if (ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = ret.GetGenericArguments()[0];
            var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner);
            return fromResult.Invoke(null, [DefaultValue(inner)]);
        }
        return DefaultValue(ret);
    }

    private static object? DefaultValue(Type t)
        => t.IsValueType ? Activator.CreateInstance(t) : null;
}

internal sealed class F02ScopeContextStub(UserScopeAccess access) : IScopeContext
{
    public static F02ScopeContextStub Global() => new(new UserScopeAccess { HasGlobalAccess = true });

    public static F02ScopeContextStub ForClients(params Guid[] clientIds)
        => new(new UserScopeAccess { HasGlobalAccess = false, AllowedClientIds = clientIds });

    public static F02ScopeContextStub ForSites(params Guid[] siteIds)
        => new(new UserScopeAccess { HasGlobalAccess = false, AllowedSiteIds = siteIds });

    public static F02ScopeContextStub Empty()
        => new(new UserScopeAccess { HasGlobalAccess = false });

    public Guid? UserId => null;
    public Guid? ResolvedClientId { get; set; } = access.AllowedClientIds.FirstOrDefault();
    public Guid? ResolvedSiteId { get; set; } = access.AllowedSiteIds.FirstOrDefault();
    public void SetUserId(Guid userId) { }

    public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action)
        => Task.FromResult(access);

    public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action)
        => Task.FromResult(access.HasGlobalAccess);
}

internal sealed class F02SiteRepositoryStub : ISiteRepository
{
    private readonly Dictionary<Guid, Site> _sites = new();

    public F02SiteRepositoryStub(params Site[] sites)
    {
        foreach (var site in sites)
            _sites[site.Id] = site;
    }

    public Task<Site?> GetByIdAsync(Guid id)
        => Task.FromResult(_sites.TryGetValue(id, out var site)
            ? site
            : (_sites.Count == 0 ? new Site { Id = id, ClientId = Guid.NewGuid(), Name = "site" } : null));

    public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false)
        => Task.FromResult(_sites.Values.Where(s => s.ClientId == clientId).AsEnumerable());

    public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false)
    {
        var set = clientIds.ToHashSet();
        return Task.FromResult(_sites.Values.Where(s => set.Contains(s.ClientId)).AsEnumerable());
    }

    public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false)
        => Task.FromResult(_sites.Values.AsEnumerable());

    public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false)
    {
        var set = siteIds.ToHashSet();
        return Task.FromResult(_sites.Values.Where(s => set.Contains(s.Id)).AsEnumerable());
    }

    public Task<Site> CreateAsync(Site site)
    {
        _sites[site.Id] = site;
        return Task.FromResult(site);
    }

    public Task UpdateAsync(Site site)
    {
        _sites[site.Id] = site;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id)
    {
        _sites.Remove(id);
        return Task.CompletedTask;
    }
}

internal sealed class F02AgentRepositoryStub : IAgentRepository
{
    private readonly Dictionary<Guid, Agent> _agents = new();

    public F02AgentRepositoryStub(params Agent[] agents)
    {
        foreach (var agent in agents)
            _agents[agent.Id] = agent;
    }

    public Task<Agent?> GetByIdAsync(Guid id)
        => Task.FromResult(_agents.TryGetValue(id, out var agent) ? agent : null);

    public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Agent>>(_agents.Values.Where(a => ids.Contains(a.Id)).ToList());

    public Task<IEnumerable<Agent>> GetAllAsync() => Task.FromResult(_agents.Values.AsEnumerable());
    public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => Task.FromResult(_agents.Values.Where(a => a.SiteId == siteId).AsEnumerable());
    public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => Task.FromResult(Enumerable.Empty<Agent>());
    public Task<Agent> CreateAsync(Agent agent) { _agents[agent.Id] = agent; return Task.FromResult(agent); }
    public Task UpdateAsync(Agent agent) { _agents[agent.Id] = agent; return Task.CompletedTask; }
    public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => Task.CompletedTask;
    public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([]);
    public Task<IReadOnlyList<Agent>> GetOnlineIncludingTrashedAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([]);
    public Task<Agent?> GetByIdForDecommissionAsync(Guid id) => GetByIdAsync(id);
    public Task ApproveZeroTouchAsync(Guid agentId) => Task.CompletedTask;
    public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => Task.CompletedTask;
    public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => Task.CompletedTask;
    public Task DeleteAsync(Guid id) { _agents.Remove(id); return Task.CompletedTask; }
    public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Agent>>([]);
}

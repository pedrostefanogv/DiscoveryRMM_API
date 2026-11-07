using Discovery.Api.Services;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Regressão do achado P0-1 da auditoria de identidade (2026-11-07): o início e o
/// controle de sessão remota não validavam o escopo (client/site) do chamador.
///
/// O endpoint declara RequirePermission(Agents, Execute) com escopo Global, que
/// apenas prova que a permissão existe em ALGUM lugar; o tenant era DERIVADO do
/// agente e nunca comparado com o chamador. Na prática, um técnico escopado ao
/// cliente A abria sessão remota em qualquer máquina do cliente B.
///
/// Estes testes fixam a regra: a restrição vale no SERVIDOR, não no menu.
/// </summary>
[TestFixture]
public class RemoteSessionScopePolicyTests
{
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SiteA1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SiteB1 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private static UserScopeAccess Access(
        bool global = false, Guid[]? clients = null, Guid[]? sites = null) => new()
    {
        HasGlobalAccess = global,
        AllowedClientIds = clients ?? [],
        AllowedSiteIds = sites ?? []
    };

    [Test]
    public void Global_access_allows_any_agent()
        => Assert.That(RemoteSessionScopePolicy.Allows(Access(global: true), ClientB, SiteB1), Is.True);

    [Test]
    public void Client_scoped_access_allows_agent_of_same_client()
        => Assert.That(RemoteSessionScopePolicy.Allows(Access(clients: [ClientA]), ClientA, SiteA1), Is.True);

    [Test]
    public void Client_scoped_access_denies_agent_of_another_client()
        => Assert.That(RemoteSessionScopePolicy.Allows(Access(clients: [ClientA]), ClientB, SiteB1), Is.False);

    [Test]
    public void Site_scoped_access_allows_only_that_site()
    {
        Assert.That(RemoteSessionScopePolicy.Allows(Access(sites: [SiteA1]), ClientA, SiteA1), Is.True);
        // Mesmo cliente, outro site: atribuição de Site não herda para o cliente todo.
        Assert.That(RemoteSessionScopePolicy.Allows(Access(sites: [SiteA1]), ClientA, Guid.NewGuid()), Is.False);
    }

    [Test]
    public void No_scope_at_all_is_denied()
        => Assert.That(RemoteSessionScopePolicy.Allows(Access(), ClientA, SiteA1), Is.False);

    [Test]
    public void Null_access_is_denied()
        => Assert.That(RemoteSessionScopePolicy.Allows(null, ClientA, SiteA1), Is.False);

    [Test]
    public void Empty_guids_never_match_a_scope_list()
    {
        // Um site/cliente não resolvido (Guid.Empty) não pode virar acesso liberado,
        // mesmo que a lista contenha Guid.Empty.
        Assert.That(RemoteSessionScopePolicy.Allows(Access(clients: [Guid.Empty], sites: [Guid.Empty]), Guid.Empty, Guid.Empty),
            Is.False);
    }
}

/// <summary>
/// Cobre o <see cref="RemoteSessionScopeResolver"/>, que é o caminho único usado
/// pelo filtro (todos os endpoints de sessão) e pelo handler de start.
/// </summary>
[TestFixture]
public class RemoteSessionScopeResolverTests
{
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SiteA1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static (RemoteSessionScopeResolver Resolver, ScopeStub Scope) Build(Agent? agent, Site? site, UserScopeAccess access)
    {
        var scope = new ScopeStub(access);
        var resolver = new RemoteSessionScopeResolver(new AgentRepoStub(agent), new SiteRepoStub(site), scope);
        return (resolver, scope);
    }

    [Test]
    public async Task Agent_from_another_client_is_denied()
    {
        var agent = new Agent { Id = Guid.NewGuid(), SiteId = SiteA1 };
        var site = new Site { Id = SiteA1, ClientId = ClientA };
        var (resolver, _) = Build(agent, site, new UserScopeAccess { AllowedClientIds = [ClientB] });

        Assert.That(await resolver.CanAccessAgentAsync(Guid.NewGuid(), agent.Id, isExecute: true), Is.False);
    }

    [Test]
    public async Task Agent_from_the_allowed_client_is_permitted()
    {
        var agent = new Agent { Id = Guid.NewGuid(), SiteId = SiteA1 };
        var site = new Site { Id = SiteA1, ClientId = ClientA };
        var (resolver, _) = Build(agent, site, new UserScopeAccess { AllowedClientIds = [ClientA] });

        Assert.That(await resolver.CanAccessAgentAsync(Guid.NewGuid(), agent.Id, isExecute: true), Is.True);
    }

    [Test]
    public async Task Global_access_short_circuits_without_loading_the_agent()
    {
        // Sem agente no repositório: se o caminho global dependesse do banco, isto falharia.
        var (resolver, _) = Build(agent: null, site: null, new UserScopeAccess { HasGlobalAccess = true });

        Assert.That(await resolver.CanAccessAgentAsync(Guid.NewGuid(), Guid.NewGuid(), isExecute: true), Is.True);
    }

    [Test]
    public async Task Missing_agent_is_denied()
    {
        var (resolver, _) = Build(agent: null, site: null, new UserScopeAccess { AllowedClientIds = [ClientA] });
        Assert.That(await resolver.CanAccessAgentAsync(Guid.NewGuid(), Guid.NewGuid(), isExecute: true), Is.False);
    }

    [Test]
    public async Task Without_any_agents_scope_reports_missing_resource_access_not_out_of_scope()
    {
        // Distinção que importa para o status HTTP: "não tem a permissão" deve virar
        // 403 (RequirePermission responde), não 404 (escopo). Antes os dois casos
        // colapsavam num 404, o que confundia quem simplesmente não tem acesso.
        var agent = new Agent { Id = Guid.NewGuid(), SiteId = SiteA1 };
        var site = new Site { Id = SiteA1, ClientId = ClientA };
        var (resolver, _) = Build(agent, site, new UserScopeAccess());

        var decision = await resolver.EvaluateAgentAsync(Guid.NewGuid(), agent.Id, isExecute: true);

        Assert.That(decision, Is.EqualTo(RemoteSessionScopeDecision.WithoutResourceAccess));
    }

    [Test]
    public async Task Agent_from_another_client_reports_out_of_scope()
    {
        var agent = new Agent { Id = Guid.NewGuid(), SiteId = SiteA1 };
        var site = new Site { Id = SiteA1, ClientId = ClientA };
        var (resolver, _) = Build(agent, site, new UserScopeAccess
        {
            AllowedClientIds = [Guid.Parse("99999999-9999-9999-9999-999999999999")]
        });

        var decision = await resolver.EvaluateAgentAsync(Guid.NewGuid(), agent.Id, isExecute: true);

        Assert.That(decision, Is.EqualTo(RemoteSessionScopeDecision.DeniedOutOfScope));
    }

    [Test]
    public async Task Global_access_reports_allowed()
    {
        var agent = new Agent { Id = Guid.NewGuid(), SiteId = SiteA1 };
        var site = new Site { Id = SiteA1, ClientId = ClientA };
        var (resolver, _) = Build(agent, site, new UserScopeAccess { HasGlobalAccess = true });

        Assert.That(await resolver.EvaluateAgentAsync(Guid.NewGuid(), agent.Id, isExecute: true),
            Is.EqualTo(RemoteSessionScopeDecision.Allowed));
    }

    [Test]
    public async Task Resolves_tenant_from_the_agent_site()
    {
        var agent = new Agent { Id = Guid.NewGuid(), SiteId = SiteA1 };
        var site = new Site { Id = SiteA1, ClientId = ClientA };
        var (resolver, _) = Build(agent, site, new UserScopeAccess { HasGlobalAccess = true });

        var tenant = await resolver.ResolveAgentTenantAsync(agent.Id);
        Assert.That(tenant, Is.Not.Null);
        Assert.That(tenant!.Value.ClientId, Is.EqualTo(ClientA));
        Assert.That(tenant.Value.SiteId, Is.EqualTo(SiteA1));
    }

    private sealed class ScopeStub(UserScopeAccess access) : IScopeContext
    {
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
        public void SetUserId(Guid userId) { }
        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action) => Task.FromResult(access);
        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action) => Task.FromResult(access.HasGlobalAccess);
    }

    private sealed class AgentRepoStub(Agent? agent) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult(agent);
        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);
        public Task<IEnumerable<Agent>> GetAllAsync() => Task.FromResult<IEnumerable<Agent>>([]);
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => Task.FromResult<IEnumerable<Agent>>([]);
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => Task.FromResult<IEnumerable<Agent>>([]);
        public Task<Agent> CreateAsync(Agent agent) => Task.FromResult(agent);
        public Task UpdateAsync(Agent agent) => Task.CompletedTask;
        public Task UpdateStatusAsync(Guid id, Discovery.Core.Enums.AgentStatus status, string? ipAddress) => Task.CompletedTask;
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([]);
        public Task ApproveZeroTouchAsync(Guid agentId) => Task.CompletedTask;
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => Task.CompletedTask;
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);
    }

    private sealed class SiteRepoStub(Site? site) : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id) => Task.FromResult(site);
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) => Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>([]);
        public Task<Site> CreateAsync(Site site) => Task.FromResult(site);
        public Task UpdateAsync(Site site) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }
}

using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Exceptions;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Governança das MCP tools por escopo: precedência (agent > site > client >
/// global), bloqueio de herança (Locked), upsert/reset e rate limit.
/// </summary>
[TestFixture]
public class McpToolPolicyGovernanceTests
{
    // ── Repositório: precedência e bloqueio ─────────────────────────────────

    [Test]
    public async Task Effective_AgentPolicyOverridesGlobal()
    {
        await using var db = NewDb();
        var agentId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        db.Add(Global("buscar_x", enabled: true));
        db.Add(new McpToolPolicy
        {
            Id = Guid.NewGuid(), ToolName = "buscar_x", AgentId = agentId, IsEnabled = false,
        });
        await db.SaveChangesAsync();

        var repo = new McpToolPolicyRepository(db);

        var effective = await repo.GetEffectivePoliciesAsync(clientId, siteId, agentId);
        Assert.That(effective.Single(p => p.ToolName == "buscar_x").IsEnabled, Is.False,
            "a política do agente deve vencer a global");

        var semAgente = await repo.GetEffectivePoliciesAsync(clientId, siteId, null);
        Assert.That(semAgente.Single(p => p.ToolName == "buscar_x").IsEnabled, Is.True);
    }

    [Test]
    public async Task Effective_AgentScopeInheritsClientAndSitePolicies()
    {
        await using var db = NewDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        db.Add(new Site { Id = siteId, ClientId = clientId, Name = "Matriz" });
        db.Add(new Agent { Id = agentId, SiteId = siteId, Hostname = "PC-1" });
        // Cliente desabilita a tool; nenhuma política no site/agente.
        db.Add(new McpToolPolicy
        {
            Id = Guid.NewGuid(), ToolName = "captura_tela", ClientId = clientId, IsEnabled = false,
        });
        await db.SaveChangesAsync();

        var repo = new McpToolPolicyRepository(db);

        // Consultando SÓ pelo agente (sem clientId/siteId), a cadeia deve ser
        // resolvida e a política do cliente herdada.
        var effective = await repo.GetEffectivePoliciesAsync(null, null, agentId);
        Assert.That(effective.Single(p => p.ToolName == "captura_tela").IsEnabled, Is.False,
            "o escopo do agente deve herdar a política do cliente via site");

        // Um agente de outro site não herda essa política.
        var outroSite = Guid.NewGuid();
        var outroAgente = Guid.NewGuid();
        db.Add(new Site { Id = outroSite, ClientId = Guid.NewGuid(), Name = "Filial" });
        db.Add(new Agent { Id = outroAgente, SiteId = outroSite, Hostname = "PC-2" });
        await db.SaveChangesAsync();

        var outro = await repo.GetEffectivePoliciesAsync(null, null, outroAgente);
        Assert.That(outro.Any(p => p.ToolName == "captura_tela"), Is.False);
    }

    [Test]
    public async Task Effective_LockedGlobalBlocksSiteOverride()
    {
        await using var db = NewDb();
        var siteId = Guid.NewGuid();

        db.Add(Global("instalar_y", enabled: true, locked: true));
        db.Add(new McpToolPolicy
        {
            Id = Guid.NewGuid(), ToolName = "instalar_y", SiteId = siteId, IsEnabled = false,
        });
        await db.SaveChangesAsync();

        var effective = await new McpToolPolicyRepository(db).GetEffectivePoliciesAsync(null, siteId, null);

        Assert.That(effective.Single(p => p.ToolName == "instalar_y").IsEnabled, Is.True,
            "Locked no escopo global impede a sobrescrita pelo site");
    }

    [Test]
    public async Task Upsert_CreatesThenUpdatesSameScope()
    {
        await using var db = NewDb();
        var siteId = Guid.NewGuid();
        var repo = new McpToolPolicyRepository(db);

        await repo.UpsertAsync(new McpToolPolicy
        {
            ToolName = "ping", SiteId = siteId, IsEnabled = true, MaxCallsPerMinute = 3, TimeoutSeconds = 20,
        });
        await repo.UpsertAsync(new McpToolPolicy
        {
            ToolName = "ping", SiteId = siteId, IsEnabled = false, MaxCallsPerMinute = 9, TimeoutSeconds = 45,
        });

        var rows = await repo.GetScopePoliciesAsync(null, siteId, null);
        Assert.That(rows, Has.Count.EqualTo(1), "upsert não pode duplicar a linha do mesmo escopo");
        Assert.That(rows[0].IsEnabled, Is.False);
        Assert.That(rows[0].MaxCallsPerMinute, Is.EqualTo(9));
        Assert.That(rows[0].TimeoutSeconds, Is.EqualTo(45));
    }

    [Test]
    public async Task DeleteScopePolicy_RemovesOnlyThatScope()
    {
        await using var db = NewDb();
        var clientId = Guid.NewGuid();
        db.Add(Global("tirar_z", enabled: false));
        db.Add(new McpToolPolicy { Id = Guid.NewGuid(), ToolName = "tirar_z", ClientId = clientId, IsEnabled = true });
        await db.SaveChangesAsync();

        var repo = new McpToolPolicyRepository(db);
        Assert.That(await repo.DeleteScopePolicyAsync("tirar_z", clientId, null, null), Is.True);
        Assert.That(await repo.DeleteScopePolicyAsync("tirar_z", clientId, null, null), Is.False);

        var effective = await repo.GetEffectivePoliciesAsync(clientId, null, null);
        Assert.That(effective.Single(p => p.ToolName == "tirar_z").IsEnabled, Is.False, "volta a herdar a global");
    }

    [Test]
    public async Task EnsureGlobalPolicies_IsIdempotentAndDoesNotOverwrite()
    {
        await using var db = NewDb();
        db.Add(Global("existente", enabled: false));
        await db.SaveChangesAsync();

        var repo = new McpToolPolicyRepository(db);
        var registrations = new (string Name, string? Description)[]
        {
            ("existente", "Ferramenta existente registrada pelo agente."),
            ("nova_tool", "Descrição da nova tool."),
        };
        await repo.EnsureGlobalPoliciesAsync(McpToolSources.Agent, registrations);
        await repo.EnsureGlobalPoliciesAsync(McpToolSources.Agent, registrations);

        var rows = await repo.GetScopePoliciesAsync(null, null, null);
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Single(p => p.ToolName == "existente").IsEnabled, Is.False,
            "não pode reativar uma tool desabilitada pelo operador");
        Assert.That(rows.Single(p => p.ToolName == "existente").Description,
            Is.EqualTo("Ferramenta existente registrada pelo agente."),
            "a descrição registrada preenche a linha que ainda não tinha uma");
        Assert.That(rows.Single(p => p.ToolName == "nova_tool").Source, Is.EqualTo(McpToolSources.Agent));
        Assert.That(rows.Single(p => p.ToolName == "nova_tool").TimeoutSeconds, Is.EqualTo(120),
            "novas tools de agente nascem com o timeout padrão maior (120s)");
    }

    [Test]
    public async Task EnsureGlobalPolicies_TruncatesOverlongDescriptions()
    {
        await using var db = NewDb();
        var repo = new McpToolPolicyRepository(db);

        // create_ticket enriquecido passa de 2.3k caracteres e a coluna é
        // varchar(4000). O provider InMemory não valida tamanho, então a guarda
        // que importa é a truncagem: sem ela o Postgres derruba o lote inteiro
        // do auto-registro (EF persiste tudo em uma transação).
        await repo.EnsureGlobalPoliciesAsync(
            McpToolSources.Agent,
            new (string Name, string? Description)[] { ("create_ticket", new string('x', 5000)) });

        var row = (await repo.GetScopePoliciesAsync(null, null, null))
            .Single(p => p.ToolName == "create_ticket");

        Assert.That(row.Description, Is.Not.Null);
        Assert.That(row.Description!.Length, Is.LessThanOrEqualTo(4000));
    }

    [Test]
    public async Task SavePolicy_RejectsWhenAncestorIsLocked()
    {
        await using var db = NewDb();
        db.Add(Global("bloqueada", enabled: true, locked: true));
        await db.SaveChangesAsync();

        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new FakeExecutor());
        var clientId = Guid.NewGuid();

        await Assert.ThrowsAsync<McpToolPolicyLockedException>(async () =>
            await governance.SavePolicyAsync("bloqueada",
                new SaveMcpToolPolicyRequest(clientId, null, null, IsEnabled: false,
                    MaxCallsPerMinute: null, TimeoutSeconds: null, Locked: false)));
    }

    [Test]
    public async Task SavePolicy_AllowsOverrideWhenAncestorIsNotLocked()
    {
        await using var db = NewDb();
        db.Add(Global("livre", enabled: true, locked: false));
        await db.SaveChangesAsync();

        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new FakeExecutor());
        var clientId = Guid.NewGuid();

        var catalog = await governance.SavePolicyAsync("livre",
            new SaveMcpToolPolicyRequest(clientId, null, null, IsEnabled: false,
                MaxCallsPerMinute: null, TimeoutSeconds: null, Locked: false));

        var item = catalog.Tools.Single(t => t.Name == "livre");
        Assert.That(item.IsEnabled, Is.False);
        Assert.That(item.OverriddenHere, Is.True);
    }

    // ── Governança: catálogo e rate limit ───────────────────────────────────

    [Test]
    public async Task Catalog_MarksOverriddenAndDisabledTools()
    {
        await using var db = NewDb();
        var siteId = Guid.NewGuid();
        db.Add(Global("knowledge_search", enabled: true));
        db.Add(Global("get_inventory", enabled: true));
        db.Add(new McpToolPolicy
        {
            Id = Guid.NewGuid(), ToolName = "get_inventory", SiteId = siteId, IsEnabled = false,
        });
        await db.SaveChangesAsync();

        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new FakeExecutor());
        var catalog = await governance.GetCatalogAsync(new McpToolScope(null, siteId, null));

        var inv = catalog.Tools.Single(t => t.Name == "get_inventory");
        Assert.That(inv.IsEnabled, Is.False);
        Assert.That(inv.OverriddenHere, Is.True);

        var kb = catalog.Tools.Single(t => t.Name == "knowledge_search");
        Assert.That(kb.IsEnabled, Is.True);
        Assert.That(kb.OverriddenHere, Is.False);
        Assert.That(kb.Source, Is.EqualTo(McpToolSources.Server));
    }

    [Test]
    public async Task Catalog_IncludesAgentToolsRegisteredAsGlobalPolicies()
    {
        await using var db = NewDb();
        await new McpToolPolicyRepository(db).EnsureGlobalPoliciesAsync(
            McpToolSources.Agent,
            new (string Name, string? Description)[] { ("service_control", "Controla serviços do Windows.") });

        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new FakeExecutor());
        var catalog = await governance.GetCatalogAsync(new McpToolScope(null, null, null));

        var tool = catalog.Tools.Single(t => t.Name == "service_control");
        Assert.That(tool.Source, Is.EqualTo(McpToolSources.Agent));
        Assert.That(tool.IsEnabled, Is.True);
        Assert.That(tool.Description, Is.EqualTo("Controla serviços do Windows."),
            "a descrição registrada pelo agente é exibida no catálogo com escopo global");
    }

    [Test]
    public async Task Catalog_ExposesCategoryWhenToUseAndTimeoutRecommendations()
    {
        await using var db = NewDb();
        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new FakeExecutor());

        var catalog = await governance.GetCatalogAsync(new McpToolScope(null, null, null));

        // Sem política local, o catálogo mostra a recomendação da plataforma
        // (30s para busca na KB) em vez do antigo padrão cego de 10s.
        var kb = catalog.Tools.Single(t => t.Name == "knowledge_search");
        Assert.That(kb.Category, Is.EqualTo("Base de conhecimento"));
        Assert.That(kb.WhenToUse, Is.Not.Null.And.Not.Empty);
        Assert.That(kb.RecommendedTimeoutSeconds, Is.EqualTo(30));
        Assert.That(kb.TimeoutApplies, Is.True);

        // Tool do agente conhecida: recomendação específica da carga (90s).
        var inv = catalog.Tools.Single(t => t.Name == "get_inventory");
        Assert.That(inv.RecommendedTimeoutSeconds, Is.EqualTo(90));
        Assert.That(inv.Category, Is.EqualTo("Inventário"));
    }

    [Test]
    public async Task Catalog_MarksInteractiveToolsAsTimeoutNotApplicable()
    {
        await using var db = NewDb();
        db.Add(new McpToolPolicy
        {
            Id = Guid.NewGuid(),
            ToolName = "ask_user",
            Source = McpToolSources.Agent,
            IsEnabled = true,
        });
        await db.SaveChangesAsync();

        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new FakeExecutor());
        var catalog = await governance.GetCatalogAsync(new McpToolScope(null, null, null));

        var ask = catalog.Tools.Single(t => t.Name == "ask_user");
        Assert.That(ask.TimeoutApplies, Is.False,
            "ask_user aguarda o usuário: o timeout configurado é ignorado em runtime");
        Assert.That(ask.Category, Is.EqualTo("Interação"));
    }

    [Test]
    public async Task RateLimit_LocalFallback_BlocksAfterMaxCallsPerMinute()
    {
        // Sem Redis (null) → usa o contador local, que é o fallback.
        var governance = new McpToolGovernance(null!, null!, redis: null);
        var scope = new McpToolScope(Guid.NewGuid(), null, null);
        var tool = "rate_tool_" + Guid.NewGuid().ToString("N");

        Assert.That(await governance.TryConsumeRateLimitAsync(tool, scope, 2), Is.True);
        Assert.That(await governance.TryConsumeRateLimitAsync(tool, scope, 2), Is.True);
        Assert.That(await governance.TryConsumeRateLimitAsync(tool, scope, 2), Is.False, "3ª chamada excede o limite de 2/min");
    }

    [Test]
    public async Task RateLimit_ZeroMeansUnlimited()
    {
        var governance = new McpToolGovernance(null!, null!, redis: null);
        var tool = "rate_unlimited_" + Guid.NewGuid().ToString("N");

        for (var i = 0; i < 50; i++)
            Assert.That(await governance.TryConsumeRateLimitAsync(tool, new McpToolScope(null, null, null), 0), Is.True);
    }

    [Test]
    public async Task RateLimit_UsesRedisCounterWhenAvailable()
    {
        var redis = new FakeRedisService();
        var governance = new McpToolGovernance(null!, null!, redis);
        var tool = "rate_redis_" + Guid.NewGuid().ToString("N");
        var scope = new McpToolScope(null, null, null);

        Assert.That(await governance.TryConsumeRateLimitAsync(tool, scope, 2), Is.True);
        Assert.That(await governance.TryConsumeRateLimitAsync(tool, scope, 2), Is.True);
        Assert.That(await governance.TryConsumeRateLimitAsync(tool, scope, 2), Is.False);
        Assert.That(redis.Expiries, Is.Not.Empty, "a primeira chamada deve definir o TTL de 60s");
    }

    [Test]
    public async Task RateLimit_FallsBackToLocalWhenRedisUnavailable()
    {
        var redis = new FakeRedisService { Unavailable = true };
        var governance = new McpToolGovernance(null!, null!, redis);
        var tool = "rate_redis_down_" + Guid.NewGuid().ToString("N");

        Assert.That(await governance.TryConsumeRateLimitAsync(tool, new McpToolScope(null, null, null), 1), Is.True);
        Assert.That(await governance.TryConsumeRateLimitAsync(tool, new McpToolScope(null, null, null), 1), Is.False,
            "Redis fora do ar não pode desligar o rate limit");
    }

    /// <summary>Redis falso: contadores em memória + registro de expirações.</summary>
    private sealed class FakeRedisService : IRedisService
    {
        private readonly Dictionary<string, long> _counters = new();
        public bool Unavailable { get; init; }
        public List<string> Expiries { get; } = new();

        public bool IsConnected => !Unavailable;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<long> IncrementAsync(string key)
        {
            if (Unavailable) return Task.FromResult(0L);
            _counters[key] = _counters.GetValueOrDefault(key) + 1;
            return Task.FromResult(_counters[key]);
        }
        public Task<long> IncrementByAsync(string key, long amount) => IncrementAsync(key);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.CompletedTask;
        public Task<bool> SetExpiryAsync(string key, int expirySeconds)
        {
            Expiries.Add(key);
            return Task.FromResult(true);
        }
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(60);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static McpToolPolicy Global(string tool, bool enabled, bool locked = false) => new()
    {
        Id = Guid.NewGuid(),
        ToolName = tool,
        IsEnabled = enabled,
        Locked = locked,
        Source = McpToolSources.Server,
    };

    private static McpPolicyTestDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"mcp-policies-{Guid.NewGuid():N}")
            .Options;
        return new McpPolicyTestDbContext(options);
    }

    /// <summary>Contexto restrito a McpToolPolicy/Site/Agent (InMemory não suporta o modelo completo).</summary>
    private sealed class McpPolicyTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(McpToolPolicy).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(McpToolPolicy) && type != typeof(Site) && type != typeof(Agent)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<McpToolPolicy>(entity => entity.HasKey(p => p.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(s => s.Id));
            modelBuilder.Entity<Agent>(entity => entity.HasKey(a => a.Id));
        }
    }

    /// <summary>Executor falso: expõe um catálogo mínimo de tools de servidor.</summary>
    private sealed class FakeExecutor : IMcpToolExecutor
    {
        private static readonly IReadOnlyList<(string Name, string Description)> Catalog =
        [
            ("knowledge_search", "Busca na base de conhecimento."),
            ("get_inventory", "Inventário do computador."),
        ];

        public void RegisterHandler(string toolName, Func<McpToolCallContext, Task<string>> handler) { }

        public Task<string> ExecuteAsync(
            string toolName, string argumentsJson, Guid? clientId, Guid? siteId, Guid? agentId,
            AIIntegrationSettings aiSettings, IReadOnlyCollection<Guid>? excludeArticleIds = null,
            Guid? departmentId = null, Guid? sessionId = null, CancellationToken ct = default) =>
            Task.FromResult("{}");

        public Task<List<LlmTool>> GetAvailableToolsAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult(new List<LlmTool>());

        public IReadOnlyList<(string Name, string Description)> GetServerCatalog() => Catalog;

        public Task<bool> IsToolEnabledAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    // ── Capacidade governável (A2UI) ────────────────────────────────────────

    [Test]
    public async Task Catalog_ListsA2uiCapability_WithoutExposingItAsTool()
    {
        await using var db = NewDb();
        var clientId = Guid.NewGuid();
        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new FakeExecutor());

        var catalog = await governance.GetCatalogAsync(new McpToolScope(clientId, null, null));
        var item = catalog.Tools.SingleOrDefault(t => t.Name == McpToolCatalogMetadata.A2uiCapability);

        Assert.That(item, Is.Not.Null, "a capacidade a2ui precisa aparecer na tela de governança");
        Assert.That(item!.IsEnabled, Is.True, "sem política, herda habilitada");
        Assert.That(item.Description, Does.Contain("Interface rica"));
    }

    [Test]
    public async Task Catalog_DisablingA2uiOnClient_AppliesToScope()
    {
        await using var db = NewDb();
        var clientId = Guid.NewGuid();
        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new FakeExecutor());

        await governance.SavePolicyAsync(
            McpToolCatalogMetadata.A2uiCapability,
            new SaveMcpToolPolicyRequest(clientId, null, null, IsEnabled: false,
                MaxCallsPerMinute: null, TimeoutSeconds: null, Locked: false));

        var catalog = await governance.GetCatalogAsync(new McpToolScope(clientId, null, null));
        var capability = catalog.Tools.Single(t => t.Name == McpToolCatalogMetadata.A2uiCapability);

        Assert.That(capability.IsEnabled, Is.False);
        // Salvar não pode "rebaixar" a capacidade para ferramenta do agente nem
        // inventar rate limit/timeout: ela não é executável.
        Assert.That(capability.Source, Is.EqualTo(McpToolSources.Server));
        Assert.That(capability.MaxCallsPerMinute, Is.EqualTo(0));
        Assert.That(capability.TimeoutSeconds, Is.EqualTo(0));
    }

    [Test]
    public void Capability_IsGovernedButNeverACallableTool()
    {
        Assert.That(McpToolCatalogMetadata.GovernableCapabilities, Does.Contain(McpToolCatalogMetadata.A2uiCapability));
        Assert.That(McpToolCatalogMetadata.CapabilityDescription(McpToolCatalogMetadata.A2uiCapability), Is.Not.Null);
        Assert.That(McpToolCatalogMetadata.CapabilityDescription("knowledge_search"), Is.Null);
        // Sem handler, GetAvailableToolsAsync nunca expõe a capacidade ao LLM.
        Assert.That(new FakeExecutor().GetServerCatalog().Any(t => t.Name == McpToolCatalogMetadata.A2uiCapability), Is.False);
    }
}
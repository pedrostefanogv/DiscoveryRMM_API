using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.AgentAuth.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Kill switch da aba "Atualizações" (updatesTabEnabled).
///
/// Contrato: global no servidor com default TRUE (zero mudanca de comportamento
/// no rollout), sobrescrivivel por cliente e site (NULL = herda) e resolvido na
/// precedencia site ?? client ?? server — igual as demais flags de aba.
/// Cobre default, persistencia por PATCH, cascata no payload do agent,
/// ausencia do campo no payload e o ResolvedConfiguration/Inheritance.
/// </summary>
public class UpdatesTabEnabledTests
{
    // ── (a) Default do servidor = true ────────────────────────────

    [Test]
    public void Default_do_servidor_e_true()
    {
        Assert.That(new ServerConfiguration().UpdatesTabEnabled, Is.True);
    }

    [Test]
    public void Cliente_e_site_iniciam_herdando()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new ClientConfiguration().UpdatesTabEnabled, Is.Null);
            Assert.That(new SiteConfiguration().UpdatesTabEnabled, Is.Null);
        });
    }

    [Test]
    public void Campo_esta_no_catalogo_de_campos_gerenciados()
    {
        // Sem entrar no ManagedFields o nome nao e reconhecido no lockedFieldsJson.
        Assert.That(ConfigurationFieldCatalog.ManagedFields, Does.Contain("UpdatesTabEnabled"));
        Assert.That(
            ConfigurationFieldCatalog.NormalizeFieldName(nameof(ServerConfiguration.UpdatesTabEnabled)),
            Is.EqualTo("UpdatesTabEnabled"));
    }

    // ── (b) PatchServerAsync grava o campo ────────────────────────

    [Test]
    public async Task Patch_do_servidor_grava_updates_tab_enabled()
    {
        // Contexto novo por chamada: PatchServerAsync faz _db.Update(config) e o
        // segundo PATCH sobre o MESMO contexto re-anexaria a entidade ja rastreada
        // (IdentityMap conflict) — artefato de harness, nao do campo.
        await using (var db = CreateDb())
        {
            var service = BuildService(db);
            await service.PatchServerAsync(new Dictionary<string, object> { ["UpdatesTabEnabled"] = false });

            var saved = await db.ServerConfigurations.AsNoTracking().SingleAsync();
            Assert.That(saved.UpdatesTabEnabled, Is.False);
        }

        await using (var db = CreateDb())
        {
            var service = BuildService(db);
            await service.PatchServerAsync(new Dictionary<string, object> { ["UpdatesTabEnabled"] = true });

            var reopened = await db.ServerConfigurations.AsNoTracking().SingleAsync();
            Assert.That(reopened.UpdatesTabEnabled, Is.True);
        }
    }

    [Test]
    public async Task Patch_de_cliente_grava_e_reset_volta_a_herdar()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ClientConfigurations.Add(new ClientConfiguration { Id = IdGenerator.NewId(), ClientId = clientId });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchClientAsync(clientId, new Dictionary<string, object> { ["UpdatesTabEnabled"] = false });

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.UpdatesTabEnabled, Is.False, "PATCH de cliente precisa persistir o override");

        await service.ResetClientPropertyAsync(clientId, nameof(ClientConfiguration.UpdatesTabEnabled));

        var reset = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(reset.UpdatesTabEnabled, Is.Null, "reset precisa voltar a herdar (null)");
    }

    [Test]
    public async Task Patch_de_site_grava_o_override()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchSiteAsync(siteId, new Dictionary<string, object> { ["UpdatesTabEnabled"] = false });

        var saved = await db.SiteConfigurations.AsNoTracking().SingleAsync(s => s.SiteId == siteId);
        Assert.That(saved.UpdatesTabEnabled, Is.False);
    }

    [Test]
    public async Task Campo_bloqueado_no_servidor_nao_pode_ser_sobrescrito_mas_aceita_null()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            LockedFieldsJson = "[\"UpdatesTabEnabled\"]",
        });
        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            UpdatesTabEnabled = false,
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PatchClientAsync(
            clientId, new Dictionary<string, object> { ["UpdatesTabEnabled"] = true }));

        // null = volta a herdar: permitido mesmo com o campo bloqueado no servidor.
        await service.PatchClientAsync(clientId, new Dictionary<string, object> { ["UpdatesTabEnabled"] = null });

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.UpdatesTabEnabled, Is.Null);
    }

    // ── (c)/(d) Payload do agent: cascata e ausencia ──────────────

    [Test]
    public async Task Payload_do_agent_usa_site_depois_cliente_depois_servidor()
    {
        await using var db = CreateDb();
        var (agentId, siteId) = await SeedAsync(
            db, serverUpdates: true, clientUpdates: true, siteUpdates: false);

        var payload = await BuildPayloadAsync(db, agentId);

        Assert.That(payload["updatesTabEnabled"], Is.EqualTo(false), "site vence cliente e servidor");

        // Site herda do cliente
        db.SiteConfigurations.RemoveRange(db.SiteConfigurations);
        await db.SaveChangesAsync();
        var fromClient = await BuildPayloadAsync(db, agentId);
        Assert.That(fromClient["updatesTabEnabled"], Is.EqualTo(true));

        // Sem cliente, herda do servidor
        db.ClientConfigurations.RemoveRange(db.ClientConfigurations);
        await db.SaveChangesAsync();
        var fromServer = await BuildPayloadAsync(db, agentId);
        Assert.That(fromServer["updatesTabEnabled"], Is.EqualTo(true));
    }

    [Test]
    public async Task Payload_do_agent_site_desligado_vence_cliente_ligado()
    {
        await using var db = CreateDb();
        var (agentId, _) = await SeedAsync(db, serverUpdates: true, clientUpdates: true, siteUpdates: false);

        var payload = await BuildPayloadAsync(db, agentId);

        Assert.Multiple(() =>
        {
            Assert.That(payload.ContainsKey("updatesTabEnabled"), Is.True, "a chave precisa existir sempre");
            Assert.That(payload["updatesTabEnabled"], Is.EqualTo(false));
        });
    }

    [Test]
    public async Task Ausencia_do_campo_no_payload_nao_desliga_a_aba()
    {
        // Servidor default (true) e nenhum override: o payload envia true e a aba
        // continua visivel. A chave nunca chega null/false por omissao.
        await using var db = CreateDb();
        var (agentId, _) = await SeedAsync(db, serverUpdates: true, clientUpdates: null, siteUpdates: null);

        var payload = await BuildPayloadAsync(db, agentId);

        Assert.Multiple(() =>
        {
            Assert.That(payload.ContainsKey("updatesTabEnabled"), Is.True);
            Assert.That(payload["updatesTabEnabled"], Is.EqualTo(true),
                "sem override em nenhum nivel a aba permanece habilitada (default true)");
        });
    }

    [Test]
    public async Task Servidor_desligado_propaga_para_o_agent()
    {
        await using var db = CreateDb();
        var (agentId, _) = await SeedAsync(db, serverUpdates: false, clientUpdates: null, siteUpdates: null);

        var payload = await BuildPayloadAsync(db, agentId);

        Assert.That(payload["updatesTabEnabled"], Is.EqualTo(false));
    }

    [Test]
    public void Go_lê_a_chave_exatamente_updates_tab_enabled()
    {
        // Guarda de contrato com o agent: a chave do payload e camelCase e o nome
        // C# e exatamente UpdatesTabEnabled (o Go usa a tag json:"updatesTabEnabled").
        Assert.That(
            JsonNamingPolicy.CamelCase.ConvertName(nameof(ServerConfiguration.UpdatesTabEnabled)),
            Is.EqualTo("updatesTabEnabled"));
    }

    // ── (e) ResolvedConfiguration + Inheritance ───────────────────

    [Test]
    public async Task Resolucao_marca_a_origem_do_valor()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, serverUpdates: true, clientUpdates: true, siteUpdates: false);

        var resolver = BuildResolver(db);
        var resolved = await resolver.ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.UpdatesTabEnabled, Is.False);
            Assert.That(resolved.Inheritance["UpdatesTabEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Site));
        });
    }

    [Test]
    public async Task Resolucao_herda_do_cliente_quando_o_site_e_null()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, serverUpdates: true, clientUpdates: false, siteUpdates: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.UpdatesTabEnabled, Is.False);
            Assert.That(resolved.Inheritance["UpdatesTabEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Client));
        });
    }

    [Test]
    public async Task Resolucao_cai_para_o_servidor_quando_cliente_e_site_sao_null()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, serverUpdates: false, clientUpdates: null, siteUpdates: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.UpdatesTabEnabled, Is.False);
            Assert.That(resolved.Inheritance["UpdatesTabEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Global));
        });
    }

    [Test]
    public async Task Resolucao_marca_bloqueado_quando_o_servidor_trava_o_campo()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, serverUpdates: true, clientUpdates: false, siteUpdates: true);
        var server = await db.ServerConfigurations.SingleAsync();
        server.LockedFieldsJson = "[\"UpdatesTabEnabled\"]";
        await db.SaveChangesAsync();

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.UpdatesTabEnabled, Is.True);
            Assert.That(resolved.Inheritance["UpdatesTabEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Block));
            Assert.That(resolved.BlockedFields, Does.Contain("UpdatesTabEnabled"));
        });
    }

    [Test]
    public void Resolved_configuration_tem_o_campo_declarado()
    {
        var resolved = new ResolvedConfiguration();
        resolved.UpdatesTabEnabled = false;

        Assert.That(resolved.UpdatesTabEnabled, Is.False);
        Assert.That(typeof(ResolvedConfiguration).GetProperty("UpdatesTabEnabled"), Is.Not.Null);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static async Task<(Guid AgentId, Guid SiteId)> SeedAsync(
        DiscoveryDbContext db,
        bool serverUpdates,
        bool? clientUpdates,
        bool? siteUpdates)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            UpdatesTabEnabled = serverUpdates,
        });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });
        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            UpdatesTabEnabled = clientUpdates,
        });
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
            UpdatesTabEnabled = siteUpdates,
        });
        db.Agents.Add(new Agent
        {
            Id = agentId,
            SiteId = siteId,
            Hostname = "qa-updates-tab",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return (agentId, siteId);
    }

    /// <summary>
    /// Roda o handler real do endpoint /me/configuration e devolve o dicionario
    /// efetivo (mesmo harness de repositorios/resolver dos demais testes de config).
    /// </summary>
    private static async Task<Dictionary<string, object?>> BuildPayloadAsync(DiscoveryDbContext db, Guid agentId)
    {
        var redis = new NoopRedisService();
        var serverRepo = new ServerConfigurationRepository(db, redis, EmptyConfiguration());
        var service = new ConfigurationService(
            serverRepo,
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            new NoopAuditService(),
            new NoopResolver(),
            new NoopSecretProtector(),
            redis,
            new NoopNatsValidator());

        var handler = new GetAgentConfigurationHandler(
            new AgentRepository(db),
            new SiteRepository(db),
            service,
            new NoopTlsProbe(),
            new NoopMcpPolicyRepository(),
            NullLogger<GetAgentConfigurationHandler>.Instance);

        var result = await handler.Handle(new GetAgentConfigurationQuery(agentId), CancellationToken.None);
        Assert.That(result.IsSuccess, Is.True, "o handler do payload precisa resolver o agent semeado");

        var payload = (Dictionary<string, object?>)result.Value!;
        return payload;
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"updates-tab-enabled-{Guid.NewGuid():N}")
            .Options;
        return new ReducedDbContext(options);
    }

    /// <summary>Config vazia: o repositorio so usa IConfiguration para semear defaults na 1a linha.</summary>
    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private static ConfigurationService BuildService(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        return new ConfigurationService(
            new ServerConfigurationRepository(db, redis, EmptyConfiguration()),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            new NoopAuditService(),
            new NoopResolver(),
            new NoopSecretProtector(),
            redis,
            new NoopNatsValidator());
    }

    private static ConfigurationResolver BuildResolver(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        return new ConfigurationResolver(
            new ServerConfigurationRepository(db, redis, EmptyConfiguration()),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            redis,
            new NoopSecretProtector());
    }

    /// <summary>
    /// Contexto reduzido: o modelo de producao mapeia vetores do pgvector, que o
    /// provider InMemory nao suporta (mesmo padrao de AgentHomeTabTests).
    /// </summary>
    private sealed class ReducedDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(ServerConfiguration),
                typeof(ClientConfiguration),
                typeof(SiteConfiguration),
                typeof(Client),
                typeof(Site),
                typeof(Agent),
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }
        }
    }

    private sealed class NoopTlsProbe : IAgentTlsCertificateProbe
    {
        public Task<string?> GetExpectedTlsCertHashAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetExpectedNatsTlsCertHashAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetExpectedTlsCertHashAsync(string probeUrl, string cacheKey, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public void InvalidateCache(string cacheKey) { }
    }

    private sealed class NoopMcpPolicyRepository : IMcpToolPolicyRepository
    {
        public Task<IReadOnlyList<McpToolPolicy>> GetEffectivePoliciesAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolPolicy>>([]);

        public Task<McpToolPolicy?> GetPolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<McpToolPolicy?>(null);

        public Task<IReadOnlyList<McpToolPolicy>> GetScopePoliciesAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolPolicy>>([]);

        public Task<McpToolPolicy> UpsertAsync(McpToolPolicy policy, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteScopePolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task EnsureGlobalPoliciesAsync(
            string source,
            IEnumerable<(string Name, string? Description)> tools,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> CountLowerScopeOverridesAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<IReadOnlyDictionary<string, int>> GetLowerScopeOverrideCountsAsync(
            IReadOnlyCollection<string> toolNames, Guid? clientId, Guid? siteId, Guid? agentId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Task<McpToolPolicy?> GetAncestorLockedPolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<McpToolPolicy?>(null);
    }

    private sealed class NoopRedisService : IRedisService
    {
        public bool IsConnected => false;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.CompletedTask;
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(false);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(-1);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }

    private sealed class NoopAuditService : IConfigurationAuditService
    {
        public Task LogChangeAsync(string entityType, Guid entityId, string fieldName,
            string? oldValue, string? newValue, string? reason = null, string? changedBy = null, string? ipAddress = null)
            => Task.CompletedTask;

        public Task<IEnumerable<ConfigurationAudit>> GetEntityHistoryAsync(string entityType, Guid entityId, int limit = 100)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetRecentChangesAsync(int days = 90, int limit = 1000)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetChangesByUserAsync(string username, int limit = 100)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetFieldHistoryAsync(string entityType, Guid entityId, string fieldName)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);

        public Task<IEnumerable<ConfigurationAudit>> GetAuditReportAsync(DateTime startDate, DateTime endDate)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);
    }

    private sealed class NoopResolver : IConfigurationResolver
    {
        public Task<ServerConfiguration> GetServerAsync() => throw new NotSupportedException();
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();
        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(Guid? clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => throw new NotSupportedException();
        public void ClearCache() { }
    }

    private sealed class NoopSecretProtector : ISecretProtector
    {
        public bool IsEnabled => false;
        public bool IsProtected(string? value) => false;
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
        public string UnprotectOrSelf(string? value) => value ?? string.Empty;
    }

    private sealed class NoopNatsValidator : INatsConnectionValidator
    {
        public Task<(bool IsValid, string[] Errors)> ValidateConnectionAsync(
            string url,
            string? user,
            string? password,
            CancellationToken cancellationToken)
            => Task.FromResult<(bool, string[])>((true, []));
    }
}

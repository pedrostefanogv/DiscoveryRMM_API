using Discovery.Api.DependencyInjection;
using Discovery.Api.Services.Quartz;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Discovery.Tests;

/// <summary>
/// Kill switch do job de embeddings da base de conhecimento.
///
/// ACHADO (B4): <c>KnowledgeEmbeddingEnabled</c> existia em appsettings.json:152
/// valendo <c>false</c>, mas NÃO era lido por nenhum código, e o
/// <c>KnowledgeEmbeddingJob</c> era agendado INCONDICIONALMENTE a cada 30s —
/// o flag mentia. O job foi tornado real e controlável pela UI, seguindo o modelo
/// canônico do <c>ZeroTouchEnabled</c> (herança servidor -> cliente -> site) e do
/// gate de agendamento do <c>TicketAnswerEmbeddingJob</c>.
///
/// Contrato coberto aqui:
///   (a) campo exposto como bool no escopo server, default TRUE;
///   (b) cliente/site começam herdando (null);
///   (c) o job NÃO é agendado quando a chave está false;
///   (d) o job É agendado por default (true) — comportamento atual preservado;
///   (e) precedência site ?? client ?? server no ResolvedConfiguration;
///   (f) o campo está em ManagedFields (locks/herança continuam funcionando).
/// </summary>
public class KnowledgeEmbeddingEnabledTests
{
    // ── (a)/(b) Defaults da entidade ──────────────────────────────

    [Test]
    public void Default_do_servidor_e_true()
    {
        Assert.That(new ServerConfiguration().KnowledgeEmbeddingEnabled, Is.True,
            "default true preserva o comportamento atual (o job rodava sempre)");
    }

    [Test]
    public void Cliente_e_site_iniciam_herdando()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new ClientConfiguration().KnowledgeEmbeddingEnabled, Is.Null);
            Assert.That(new SiteConfiguration().KnowledgeEmbeddingEnabled, Is.Null);
        });
    }

    [Test]
    public void Campo_esta_no_catalogo_de_campos_gerenciados()
    {
        // Sem entrar no ManagedFields o nome não é reconhecido no lockedFieldsJson.
        Assert.That(ConfigurationFieldCatalog.ManagedFields, Does.Contain("KnowledgeEmbeddingEnabled"));
        Assert.That(
            ConfigurationFieldCatalog.NormalizeFieldName(nameof(ServerConfiguration.KnowledgeEmbeddingEnabled)),
            Is.EqualTo("KnowledgeEmbeddingEnabled"));
    }

    [Test]
    public void Resolved_configuration_tem_o_campo_declarado_como_bool()
    {
        var prop = typeof(ResolvedConfiguration).GetProperty("KnowledgeEmbeddingEnabled");
        Assert.That(prop, Is.Not.Null, "o campo precisa existir no escopo de configuração resolvida");
        Assert.That(prop!.PropertyType, Is.EqualTo(typeof(bool)), "o escopo server expõe o campo como bool");
    }

    // ── (c)/(d) Gate de agendamento no Quartz ─────────────────────

    [Test]
    public async Task Job_nao_e_agendado_quando_a_chave_esta_false()
    {
        var keys = await GetScheduledJobKeysAsync(new Dictionary<string, string?>
        {
            ["BackgroundJobs:KnowledgeEmbeddingEnabled"] = "false",
        });

        Assert.That(
            keys.Any(k => k.Name == KnowledgeEmbeddingJob.Key.Name && k.Group == KnowledgeEmbeddingJob.Key.Group),
            Is.False,
            "com KnowledgeEmbeddingEnabled=false o KnowledgeEmbeddingJob não pode entrar no scheduler");
    }

    [Test]
    public async Task Job_e_agendado_por_default()
    {
        // Sem a chave no appsettings o default é true: comportamento atual preservado.
        var keys = await GetScheduledJobKeysAsync(new Dictionary<string, string?>());

        Assert.That(
            keys.Any(k => k.Name == KnowledgeEmbeddingJob.Key.Name && k.Group == KnowledgeEmbeddingJob.Key.Group),
            Is.True,
            "default true mantém o job agendado (zero mudança de comportamento no rollout)");
    }

    [Test]
    public async Task Job_e_agendado_quando_a_chave_esta_true()
    {
        var keys = await GetScheduledJobKeysAsync(new Dictionary<string, string?>
        {
            ["BackgroundJobs:KnowledgeEmbeddingEnabled"] = "true",
        });

        Assert.That(
            keys.Any(k => k.Name == KnowledgeEmbeddingJob.Key.Name && k.Group == KnowledgeEmbeddingJob.Key.Group),
            Is.True);
    }

    [Test]
    public async Task Desligar_o_job_nao_derruba_os_demais_jobs()
    {
        // Guarda de regressão: o if não pode engolir o restante do registro.
        var keys = await GetScheduledJobKeysAsync(new Dictionary<string, string?>
        {
            ["BackgroundJobs:KnowledgeEmbeddingEnabled"] = "false",
        });

        Assert.Multiple(() =>
        {
            Assert.That(keys.Any(k => k.Name == TicketAnswerEmbeddingJob.Key.Name), Is.True,
                "o job irmão de embeddings de respostas continua agendado");
            Assert.That(keys.Any(k => k.Name == AlertSchedulerJob.Key.Name), Is.True);
            Assert.That(keys.Any(k => k.Name == WingetManifestsSyncJob.Key.Name), Is.True);
        });
    }

    // ── (e) Herança servidor -> cliente -> site ───────────────────

    [Test]
    public async Task Resolucao_usa_site_depois_cliente_depois_servidor()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: true, client: true, site: false);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.KnowledgeEmbeddingEnabled, Is.False, "site vence cliente e servidor");
            Assert.That(resolved.Inheritance["KnowledgeEmbeddingEnabled"],
                Is.EqualTo((int)ConfigurationPriorityType.Site));
        });
    }

    [Test]
    public async Task Resolucao_herda_do_cliente_quando_o_site_e_null()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: true, client: false, site: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.KnowledgeEmbeddingEnabled, Is.False);
            Assert.That(resolved.Inheritance["KnowledgeEmbeddingEnabled"],
                Is.EqualTo((int)ConfigurationPriorityType.Client));
        });
    }

    [Test]
    public async Task Resolucao_cai_para_o_servidor_quando_cliente_e_site_sao_null()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: false, client: null, site: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.KnowledgeEmbeddingEnabled, Is.False);
            Assert.That(resolved.Inheritance["KnowledgeEmbeddingEnabled"],
                Is.EqualTo((int)ConfigurationPriorityType.Global));
        });
    }

    [Test]
    public async Task Resolucao_do_servidor_default_e_true()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: null, client: null, site: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.That(resolved.KnowledgeEmbeddingEnabled, Is.True,
            "sem override em nenhum nível o valor efetivo é o default true do servidor");
    }

    [Test]
    public async Task Resolucao_marca_bloqueado_quando_o_servidor_trava_o_campo()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: true, client: false, site: false);
        var server = await db.ServerConfigurations.SingleAsync();
        server.LockedFieldsJson = "[\"KnowledgeEmbeddingEnabled\"]";
        await db.SaveChangesAsync();

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.KnowledgeEmbeddingEnabled, Is.False, "o valor do site continua resolvido");
            Assert.That(resolved.Inheritance["KnowledgeEmbeddingEnabled"],
                Is.EqualTo((int)ConfigurationPriorityType.Block));
            Assert.That(resolved.BlockedFields, Does.Contain("KnowledgeEmbeddingEnabled"));
        });
    }

    // ── Persistência via PATCH ────────────────────────────────────

    [Test]
    public async Task Patch_do_servidor_grava_knowledge_embedding_enabled()
    {
        await using var db = CreateDb();
        var service = BuildService(db);
        await service.PatchServerAsync(new Dictionary<string, object> { ["KnowledgeEmbeddingEnabled"] = false });

        var saved = await db.ServerConfigurations.AsNoTracking().SingleAsync();
        Assert.That(saved.KnowledgeEmbeddingEnabled, Is.False);
    }

    [Test]
    public async Task Patch_de_cliente_grava_o_override()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ClientConfigurations.Add(new ClientConfiguration { Id = IdGenerator.NewId(), ClientId = clientId });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchClientAsync(clientId, new Dictionary<string, object> { ["KnowledgeEmbeddingEnabled"] = false });

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.KnowledgeEmbeddingEnabled, Is.False,
            "sem a cópia no UpdateAsync o PATCH/PUT era descartado");
    }

    // ── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Sobe o scheduler real com a configuração informada e devolve as JobKeys
    /// registradas (mesmo harness de QuartzRegistrationContractTests).
    /// </summary>
    private static async Task<IReadOnlyList<JobKey>> GetScheduledJobKeysAsync(
        Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDiscoveryQuartz(configuration);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await factory.GetScheduler();

        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        return keys.ToList();
    }

    private static async Task<(Guid AgentId, Guid SiteId)> SeedAsync(
        DiscoveryDbContext db,
        bool? server,
        bool? client,
        bool? site)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var serverConfig = new ServerConfiguration { Id = IdGenerator.NewId() };
        if (server.HasValue) serverConfig.KnowledgeEmbeddingEnabled = server.Value;
        db.ServerConfigurations.Add(serverConfig);

        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });
        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            KnowledgeEmbeddingEnabled = client,
        });
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
            KnowledgeEmbeddingEnabled = site,
        });
        db.Agents.Add(new Agent
        {
            Id = agentId,
            SiteId = siteId,
            Hostname = "qa-knowledge-embedding",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return (agentId, siteId);
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"knowledge-embedding-enabled-{Guid.NewGuid():N}")
            .Options;
        return new ReducedDbContext(options);
    }

    /// <summary>Config vazia: o repositório só usa IConfiguration para semear defaults na 1ª linha.</summary>
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
    /// Contexto reduzido: o modelo de produção mapeia vetores do pgvector, que o
    /// provider InMemory não suporta (mesmo padrão de UpdatesTabEnabledTests).
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

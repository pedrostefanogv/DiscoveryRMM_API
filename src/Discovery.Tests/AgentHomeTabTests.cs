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

namespace Discovery.Tests;

/// <summary>
/// Página inicial do agent (agentHomeTab): global no servidor, sobrescrevível
/// por cliente e site (NULL/vazio = herda) e normalizada para um id aceito.
/// Cobre catálogo, validação do ConfigurationService, persistência nos
/// repositórios e a resolução da herança do ConfigurationResolver.
/// </summary>
public class AgentHomeTabTests
{
    // ── Catálogo ────────────────────────────────────────────────────

    [Test]
    public void Catalogo_aceita_ids_conhecidos_e_ignora_caixa()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AgentHomeTabCatalog.IsValid("support"), Is.True);
            Assert.That(AgentHomeTabCatalog.IsValid(" Chat "), Is.True);
            Assert.That(AgentHomeTabCatalog.Normalize(" KNOWLEDGE "), Is.EqualTo("knowledge"));
        });
    }

    /// <summary>
    /// Guarda de contrato: a lista de ids precisa bater com as opções do console
    /// web (AGENT_HOME_TAB_OPTIONS) e com o catálogo do agent Go
    /// (ValidAgentHomeTabs). Mudar aqui exige mudar os outros dois.
    /// </summary>
    [Test]
    public void Catalogo_expoe_exatamente_os_ids_aceitos_na_ordem_do_console()
    {
        Assert.That(AgentHomeTabCatalog.ValidTabs, Is.EqualTo(new[]
        {
            "status",
            "store",
            "updates",
            "chat",
            "support",
            "knowledge",
        }));
    }

    [Test]
    public void Catalogo_normaliza_valor_desconhecido_para_status()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AgentHomeTabCatalog.Normalize("nao-existe"), Is.EqualTo("status"));
            Assert.That(AgentHomeTabCatalog.Normalize(null), Is.EqualTo("status"));
            Assert.That(AgentHomeTabCatalog.Normalize(""), Is.EqualTo("status"));
            Assert.That(new ServerConfiguration().AgentHomeTab, Is.EqualTo("status"));
            Assert.That(new ClientConfiguration().AgentHomeTab, Is.Null);
            Assert.That(new SiteConfiguration().AgentHomeTab, Is.Null);
        });
    }

    // ── Validação do ConfigurationService ──────────────────────────

    [Test]
    public async Task Validacao_rejeita_aba_invalida_no_servidor()
    {
        await using var db = CreateDb();
        var service = BuildService(db);

        var (isValid, errors) = await service.ValidateAsync(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            AgentHomeTab = "aba-inexistente",
        });

        Assert.Multiple(() =>
        {
            Assert.That(isValid, Is.False);
            Assert.That(errors.Any(error => error.Contains("AgentHomeTab")), Is.True);
        });
    }

    [Test]
    public async Task Validacao_rejeita_aba_vazia_no_servidor()
    {
        await using var db = CreateDb();
        var service = BuildService(db);

        var (isValid, errors) = await service.ValidateAsync(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            AgentHomeTab = string.Empty,
        });

        Assert.Multiple(() =>
        {
            Assert.That(isValid, Is.False);
            Assert.That(errors.Any(error => error.Contains("AgentHomeTab")), Is.True);
        });
    }

    [Test]
    public async Task Validacao_aceita_vazio_em_cliente_e_site_como_heranca()
    {
        await using var db = CreateDb();
        var service = BuildService(db);

        var clientResult = await service.ValidateAsync(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = Guid.NewGuid(),
            AgentHomeTab = null,
        });
        var siteResult = await service.ValidateAsync(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AgentHomeTab = string.Empty,
        });

        Assert.Multiple(() =>
        {
            Assert.That(clientResult.IsValid, Is.True);
            Assert.That(siteResult.IsValid, Is.True);
        });
    }

    // ── Persistência nos repositórios ──────────────────────────────

    [Test]
    public async Task Patch_de_cliente_persiste_agent_home_tab()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ClientConfigurations.Add(new ClientConfiguration { Id = IdGenerator.NewId(), ClientId = clientId });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchClientAsync(clientId, new Dictionary<string, object> { ["AgentHomeTab"] = "support" });

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.AgentHomeTab, Is.EqualTo("support"));
    }

    [Test]
    public async Task Reset_de_cliente_volta_a_herdar()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            AgentHomeTab = "chat",
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.ResetClientPropertyAsync(clientId, nameof(ClientConfiguration.AgentHomeTab));

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.AgentHomeTab, Is.Null);
    }

    [Test]
    public async Task Patch_de_site_persiste_agent_home_tab()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchSiteAsync(siteId, new Dictionary<string, object> { ["AgentHomeTab"] = "knowledge" });

        var saved = await db.SiteConfigurations.AsNoTracking().SingleAsync(s => s.SiteId == siteId);
        Assert.That(saved.AgentHomeTab, Is.EqualTo("knowledge"));
    }

    // ── PUT (create/update completos) também valida ────────────────

    [Test]
    public async Task Put_de_cliente_rejeita_agent_home_tab_invalido()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ClientConfigurations.Add(new ClientConfiguration { Id = IdGenerator.NewId(), ClientId = clientId });
        await db.SaveChangesAsync();

        var service = BuildService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateClientAsync(
            clientId,
            new ClientConfiguration { ClientId = clientId, AgentHomeTab = "aba-inexistente" }));

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.AgentHomeTab, Is.Null, "valor inválido não pode ser persistido pelo PUT");
    }

    [Test]
    public async Task Create_de_cliente_rejeita_agent_home_tab_invalido()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var service = BuildService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateClientConfigAsync(
            clientId,
            new ClientConfiguration { ClientId = clientId, AgentHomeTab = "aba-inexistente" }));
    }

    [Test]
    public async Task Put_de_site_rejeita_agent_home_tab_invalido()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateSiteAsync(
            siteId,
            new SiteConfiguration { SiteId = siteId, ClientId = clientId, AgentHomeTab = "aba-inexistente" }));
    }

    // ── Herança resolvida ──────────────────────────────────────────

    [Test]
    public async Task Resolucao_usa_site_depois_cliente_depois_servidor()
    {
        await using var db = CreateDb();
        var (clientId, siteId) = await SeedAsync(db, serverTab: "status", clientTab: "support", siteTab: "knowledge");

        var resolver = BuildResolver(db);
        var resolved = await resolver.ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.AgentHomeTab, Is.EqualTo("knowledge"));
            Assert.That(resolved.Inheritance["AgentHomeTab"], Is.EqualTo((int)ConfigurationPriorityType.Site));
        });

        // Site herda do cliente
        db.SiteConfigurations.RemoveRange(db.SiteConfigurations);
        await db.SaveChangesAsync();

        var fromClient = await resolver.ResolveForSiteAsync(siteId);
        Assert.Multiple(() =>
        {
            Assert.That(fromClient.AgentHomeTab, Is.EqualTo("support"));
            Assert.That(fromClient.Inheritance["AgentHomeTab"], Is.EqualTo((int)ConfigurationPriorityType.Client));
        });

        // Sem cliente, herda do servidor
        db.ClientConfigurations.RemoveRange(db.ClientConfigurations);
        await db.SaveChangesAsync();

        var fromServer = await resolver.ResolveForSiteAsync(siteId);
        Assert.Multiple(() =>
        {
            Assert.That(fromServer.AgentHomeTab, Is.EqualTo("status"));
            Assert.That(fromServer.Inheritance["AgentHomeTab"], Is.EqualTo((int)ConfigurationPriorityType.Global));
        });
    }

    [Test]
    public async Task Resolucao_normaliza_valor_invalido_para_status()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, serverTab: "status", clientTab: "lixo", siteTab: null);

        var resolver = BuildResolver(db);
        var resolved = await resolver.ResolveForSiteAsync(siteId);

        Assert.That(resolved.AgentHomeTab, Is.EqualTo("status"));
    }

    [Test]
    public async Task Resolucao_marca_bloqueado_quando_o_servidor_trava_o_campo()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, serverTab: "status", clientTab: "support", siteTab: "knowledge");
        var server = await db.ServerConfigurations.SingleAsync();
        server.LockedFieldsJson = "[\"AgentHomeTab\"]";
        await db.SaveChangesAsync();

        var resolver = BuildResolver(db);
        var resolved = await resolver.ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.AgentHomeTab, Is.EqualTo("knowledge"));
            Assert.That(resolved.Inheritance["AgentHomeTab"], Is.EqualTo((int)ConfigurationPriorityType.Block));
            Assert.That(resolved.BlockedFields, Does.Contain("AgentHomeTab"));
        });
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static async Task<(Guid ClientId, Guid SiteId)> SeedAsync(
        DiscoveryDbContext db,
        string serverTab,
        string? clientTab,
        string? siteTab)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            AgentHomeTab = serverTab,
        });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });
        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            AgentHomeTab = clientTab,
        });
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
            AgentHomeTab = siteTab,
        });
        await db.SaveChangesAsync();

        return (clientId, siteId);
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"agent-home-tab-{Guid.NewGuid():N}")
            .Options;
        return new ReducedDbContext(options);
    }

    private static ConfigurationService BuildService(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        return new ConfigurationService(
            new ServerConfigurationRepository(db, redis),
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
            new ServerConfigurationRepository(db, redis),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            redis,
            new NoopSecretProtector());
    }

    /// <summary>
    /// Contexto reduzido: o modelo de produção mapeia vetores do pgvector, que o
    /// provider InMemory não suporta (mesmo padrão de ConfigurationServiceInheritanceTests).
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

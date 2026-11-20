using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Herança da allowlist de proxy (servidor → cliente → site) e a flag que mantém
/// as entradas GLOBAIS inertes.
///
/// O contrato testado: o escopo mais específico que DEFINIR o campo substitui o
/// de cima por inteiro; ausente em todos, cai em bloqueio total. E, com a flag
/// global desligada, só o escopo servidor fica inerte — cliente/site continuam
/// valendo, que é o que permite cadastrar a lista global antes de liberá-la.
/// </summary>
[TestFixture]
public class RemoteProxyPolicyInheritanceTests
{
    private const string ProxmoxSite = """{"entries":[{"cidr":"192.168.10.0/24","ports":[8006]}]}""";
    private const string LanClient = """{"entries":[{"cidr":"10.0.0.0/8"}]}""";
    private const string GlobalOpen = """{"mode":"open"}""";

    [Test]
    public async Task Site_vence_cliente_e_servidor()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: GlobalOpen,
            client: LanClient,
            site: ProxmoxSite,
            globalEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteProxy.IsOpen, Is.False, "o site define allowlist e vence o open global");
            Assert.That(resolved.RemoteProxy.EffectiveAllowlist, Is.EqualTo(new[] { "192.168.10.0/24" }));
            Assert.That(resolved.Inheritance["RemoteProxyPolicyJson"],
                Is.EqualTo((int)ConfigurationPriorityType.Site));
        });
    }

    [Test]
    public async Task Cliente_vence_servidor_quando_site_nao_define()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: GlobalOpen,
            client: LanClient,
            site: null,
            globalEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteProxy.IsOpen, Is.False);
            Assert.That(resolved.RemoteProxy.EffectiveAllowlist, Is.EqualTo(new[] { "10.0.0.0/8" }));
            Assert.That(resolved.Inheritance["RemoteProxyPolicyJson"],
                Is.EqualTo((int)ConfigurationPriorityType.Client));
        });
    }

    [Test]
    public async Task Cai_para_o_servidor_quando_cliente_e_site_nao_definem()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: ProxmoxSite,
            client: null,
            site: null,
            globalEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteProxy.EffectiveAllowlist, Is.EqualTo(new[] { "192.168.10.0/24" }));
            Assert.That(resolved.Inheritance["RemoteProxyPolicyJson"],
                Is.EqualTo((int)ConfigurationPriorityType.Global));
        });
    }

    // ── Flag global desligada (o default do rollout) ─────────────────────

    [Test]
    public async Task Flag_global_desligada_torna_as_entradas_globais_inertes()
    {
        // Cadastro global pré-pronto, porém desativado: o caso que a feature
        // precisa suportar no primeiro momento.
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: ProxmoxSite,
            client: null,
            site: null,
            globalEnabled: false);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteProxyGlobalEntriesEnabled, Is.False);
            Assert.That(resolved.RemoteProxy.EffectiveAllowlist, Is.Empty,
                "com a flag desligada, nenhum destino global vale");
            Assert.That(resolved.RemoteProxy.IsOpen, Is.False);
        });
    }

    [Test]
    public async Task Flag_global_desligada_nao_afeta_cliente_nem_site()
    {
        // O ponto sutil: desligar as globais NÃO pode desligar as políticas
        // específicas de cliente/site — senão um cliente configurado perderia
        // acesso sem ninguém mexer nele.
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: ProxmoxSite,
            client: LanClient,
            site: null,
            globalEnabled: false);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.That(resolved.RemoteProxy.EffectiveAllowlist, Is.EqualTo(new[] { "10.0.0.0/8" }));
    }

    [Test]
    public async Task Flag_global_ligada_ativa_as_entradas_globais()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: ProxmoxSite,
            client: null,
            site: null,
            globalEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.That(resolved.RemoteProxy.EffectiveAllowlist, Is.EqualTo(new[] { "192.168.10.0/24" }));
    }

    // ── Fail-closed na resolução ─────────────────────────────────────────

    [Test]
    public async Task Sem_politica_em_nenhum_escopo_o_proxy_fica_bloqueado()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: null, client: null, site: null, globalEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteProxy.IsOpen, Is.False);
            Assert.That(resolved.RemoteProxy.EffectiveAllowlist, Is.Empty);
        });
    }

    [Test]
    public async Task Politica_corrompida_no_site_cai_em_bloqueio_total()
    {
        // Um JSON corrompido no banco não pode abrir o proxy.
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: GlobalOpen,
            client: null,
            site: "{isso-nao-e-json",
            globalEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteProxy.IsOpen, Is.False, "JSON corrompido nunca vira open");
            Assert.That(resolved.RemoteProxy.EffectiveAllowlist, Is.Empty);
        });
    }

    // ── Catálogo ─────────────────────────────────────────────────────────

    [Test]
    public void Campos_estao_no_catalogo_de_campos_gerenciados()
    {
        // Sem entrar em ManagedFields o campo não aparece na UI nem participa de
        // locks/herança — a armadilha documentada no próprio catálogo.
        Assert.Multiple(() =>
        {
            Assert.That(ConfigurationFieldCatalog.ManagedFields, Does.Contain("RemoteProxyPolicyJson"));
            Assert.That(ConfigurationFieldCatalog.ManagedFields, Does.Contain("RemoteProxyGlobalEntriesEnabled"));
        });
    }

    [Test]
    public void Entidades_declaramos_campos()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(ServerConfiguration).GetProperty("RemoteProxyPolicyJson"), Is.Not.Null);
            Assert.That(typeof(ServerConfiguration).GetProperty("RemoteProxyGlobalEntriesEnabled"), Is.Not.Null);
            Assert.That(typeof(ClientConfiguration).GetProperty("RemoteProxyPolicyJson"), Is.Not.Null);
            Assert.That(typeof(SiteConfiguration).GetProperty("RemoteProxyPolicyJson"), Is.Not.Null);
            Assert.That(typeof(Core.ValueObjects.ResolvedConfiguration).GetProperty("RemoteProxy"), Is.Not.Null);
        });
    }

    [Test]
    public void Servidor_nasce_com_bloqueio_total()
    {
        // Default "{}" = allowlist vazia, idêntico ao appsettings de hoje: a
        // migration não pode conceder acesso novo a ninguém no rollout.
        var server = new ServerConfiguration();

        Assert.Multiple(() =>
        {
            Assert.That(server.RemoteProxyPolicyJson, Is.EqualTo("{}"));
            Assert.That(server.RemoteProxyGlobalEntriesEnabled, Is.False, "cadastro global nasce inerte");
            Assert.That(RemoteProxyPolicy.Parse(server.RemoteProxyPolicyJson).Entries, Is.Empty);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<Guid> SeedAsync(
        DiscoveryDbContext db, string? server, string? client, string? site, bool globalEnabled)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            RemoteProxyPolicyJson = server ?? "{}",
            RemoteProxyGlobalEntriesEnabled = globalEnabled,
        });

        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });

        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            RemoteProxyPolicyJson = client,
        });

        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
            RemoteProxyPolicyJson = site,
        });

        await db.SaveChangesAsync();
        return siteId;
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"remote-proxy-policy-{Guid.NewGuid():N}")
            .Options;
        return new ReducedDbContext(options);
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

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

    private sealed class ReducedDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(ServerConfiguration), typeof(ClientConfiguration), typeof(SiteConfiguration),
                typeof(Client), typeof(Site), typeof(Agent),
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

    private sealed class NoopSecretProtector : ISecretProtector
    {
        public bool IsEnabled => false;
        public string Protect(string plainText) => plainText;
        public string Unprotect(string cipherText) => cipherText;
        public string UnprotectOrSelf(string? value) => value ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}

using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Criação da configuração de cliente/site do zero pela tela de proxy.
///
/// Contexto do defeito: a tela abria um cliente que NUNCA teve configuração
/// própria e recebia 404 ("Client config for &lt;guid&gt; not found"), morrendo
/// na tela de erro. Esse 404 é o estado NORMAL de um cliente novo — tudo herda
/// do nível acima —, então a tela passou a tratá-lo como "herda".
///
/// O outro lado do fluxo é o SALVAMENTO: quando o operador finalmente define uma
/// política, o PATCH precisa CRIAR a linha (não apenas atualizar). Estes testes
/// garantem que esse caminho funciona e que o valor gravado é o esperado.
/// </summary>
[TestFixture]
public class RemoteProxyPolicyFirstSaveTests
{
    private const string ProxmoxJson = """{"mode":"allowlist","entries":[{"cidr":"192.168.10.0/24","ports":[8006]}]}""";

    [Test]
    public async Task Cliente_sem_configuracao_devolve_null_antes_do_primeiro_salvamento()
    {
        // É este null que vira 404 no handler e era mostrado como erro na tela.
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();

        var service = BuildService(db);
        var config = await service.GetClientConfigAsync(clientId);

        Assert.That(config, Is.Null, "cliente novo não tem configuração própria; a tela deve tratar como 'herda'");
    }

    [Test]
    public async Task Primeiro_salvamento_de_cliente_cria_a_linha_com_a_politica()
    {
        // O PATCH faz CreateAsync quando a linha não existe. Sem isso, salvar a
        // política de um cliente novo falharia silenciosamente.
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.Sites.Add(new Site { Id = Guid.NewGuid(), ClientId = clientId, Name = "Site" });
        await db.SaveChangesAsync();

        var service = BuildService(db);

        await service.PatchClientAsync(
            clientId,
            new Dictionary<string, object> { ["RemoteProxyPolicyJson"] = ProxmoxJson },
            "tester");

        var saved = await service.GetClientConfigAsync(clientId);

        Assert.That(saved, Is.Not.Null, "o primeiro PATCH tem de criar a linha");
        var policy = RemoteProxyPolicy.Parse(saved!.RemoteProxyPolicyJson);
        Assert.That(policy.EffectiveAllowlist, Is.EqualTo(new[] { "192.168.10.0/24" }));
        Assert.That(policy.EffectiveAllowedPorts, Is.EqualTo(new[] { 8006 }));
    }

    [Test]
    public async Task Primeiro_salvamento_de_site_cria_a_linha_com_a_politica()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site" });
        await db.SaveChangesAsync();

        var service = BuildService(db);

        await service.PatchSiteAsync(
            siteId,
            new Dictionary<string, object> { ["RemoteProxyPolicyJson"] = ProxmoxJson },
            "tester");

        var saved = await service.GetSiteConfigAsync(siteId);

        Assert.That(saved, Is.Not.Null);
        Assert.That(RemoteProxyPolicy.Parse(saved!.RemoteProxyPolicyJson).EffectiveAllowlist,
            Is.EqualTo(new[] { "192.168.10.0/24" }));
    }

    [Test]
    public async Task Json_invalido_no_primeiro_salvamento_e_recusado()
    {
        // A validação estrita vale para o caminho de criação também: gravar um
        // JSON corrompido deixaria o proxy mudo sem explicação ao operador.
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();

        var service = BuildService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.PatchClientAsync(
                clientId,
                new Dictionary<string, object> { ["RemoteProxyPolicyJson"] = "{nao-e-json" },
                "tester"));

        Assert.That(ex.Message, Does.Contain("inválido"));

        // E nada foi persistido.
        Assert.That(await service.GetClientConfigAsync(clientId), Is.Null);
    }

    [Test]
    public async Task Politica_default_do_servidor_nao_afeta_cliente_novo()
    {
        // Um cliente sem linha própria não herda política nenhuma enquanto o
        // servidor estiver no default "{}": o resultado é bloqueio total, sem
        // surpresa de acesso liberado.
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site" });
        await db.SaveChangesAsync();

        var resolver = BuildResolver(db);
        var resolved = await resolver.ResolveForSiteAsync(siteId);

        Assert.That(resolved.RemoteProxy.IsOpen, Is.False);
        Assert.That(resolved.RemoteProxy.EffectiveAllowlist, Is.Empty);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"remote-proxy-first-save-{Guid.NewGuid():N}")
            .Options;
        return new ReducedDbContext(options);
    }

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
            BuildResolver(db),
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

    private sealed class NoopNatsValidator : INatsConnectionValidator
    {
        public Task<(bool IsValid, string[] Errors)> ValidateConnectionAsync(
            string url, string? user, string? password, CancellationToken cancellationToken)
            => Task.FromResult((true, Array.Empty<string>()));
    }

    private sealed class NoopSecretProtector : ISecretProtector
    {
        public bool IsEnabled => false;
        public string Protect(string plainText) => plainText;
        public string Unprotect(string cipherText) => cipherText;
        public string UnprotectOrSelf(string? value) => value ?? string.Empty;
        public bool IsProtected(string? value) => false;
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
}

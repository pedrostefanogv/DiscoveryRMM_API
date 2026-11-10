using Discovery.Api.DependencyInjection;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Discovery.Infrastructure.Services.Remote.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

/// <summary>
/// Gates de RUNTIME dos kill switches CFG-A (parte 2).
///
/// A auditoria (AUDITORIA_APPSETTINGS_VS_UI) encontrou 4 chaves que existiam no
/// appsettings e NÃO eram lidas por nenhum código — flags mortas, mesma classe de
/// bug do KnowledgeEmbeddingEnabled. Estes testes garantem que as que ganharam
/// ponto de gate CLARO realmente obedecem ao valor configurado:
///   - RemoteAccessRecordingAutoDeleteExpired -> RecordingStorageCleanupService
///   - RemoteAccessRecordingEnabled           -> RemoteRecordingService
///
/// Contrato: LIGADO mantém o comportamento de hoje; DESLIGADO impede a ação.
/// A leitura é do valor do servidor, com fail-safe para o default (true).
/// </summary>
public class FeatureKillSwitchGatesTests
{
    // ── RemoteAccessRecordingAutoDeleteExpired ────────────────────

    [Test]
    public async Task AutoDelete_desligado_nao_apaga_gravacoes_expiradas()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"rec-gate-off-{Guid.NewGuid():N}");
        Directory.CreateDirectory(basePath);
        var localFile = Path.Combine(basePath, "rec.webm");
        await File.WriteAllBytesAsync(localFile, [1, 2, 3]);

        await using var db = CreateCleanupDb();
        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            RemoteAccessRecordingAutoDeleteExpired = false,
        });

        var session = NewSession(Guid.NewGuid());
        db.RemoteSessions.Add(session);
        db.RemoteSessionRecordings.Add(NewRecording(
            session.Id, RecordingStorageProvider.Local, "rec.webm", DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        var removed = await BuildCleanup(db, basePath).CleanupExpiredAsync(DateTime.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(0), "desligado não pode remover nada");
            Assert.That(File.Exists(localFile), Is.True, "o arquivo precisa continuar no storage");
            Assert.That(db.RemoteSessionRecordings.Count(), Is.EqualTo(1), "a linha precisa continuar");
        });

        Directory.Delete(basePath, recursive: true);
    }

    [Test]
    public async Task AutoDelete_ligado_apaga_gravacoes_expiradas()
    {
        // Comportamento ATUAL preservado (default true).
        var basePath = Path.Combine(Path.GetTempPath(), $"rec-gate-on-{Guid.NewGuid():N}");
        Directory.CreateDirectory(basePath);
        var localFile = Path.Combine(basePath, "rec.webm");
        await File.WriteAllBytesAsync(localFile, [1, 2, 3]);

        await using var db = CreateCleanupDb();
        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            RemoteAccessRecordingAutoDeleteExpired = true,
        });

        var session = NewSession(Guid.NewGuid());
        db.RemoteSessions.Add(session);
        db.RemoteSessionRecordings.Add(NewRecording(
            session.Id, RecordingStorageProvider.Local, "rec.webm", DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        var removed = await BuildCleanup(db, basePath).CleanupExpiredAsync(DateTime.UtcNow);

        Assert.That(removed, Is.EqualTo(1));
        Assert.That(File.Exists(localFile), Is.False);

        Directory.Delete(basePath, recursive: true);
    }

    [Test]
    public async Task AutoDelete_sem_configuracao_mantem_o_comportamento_atual()
    {
        // Nenhuma linha de servidor: default true = apaga (nada muda para quem só
        // usa o appsettings).
        var basePath = Path.Combine(Path.GetTempPath(), $"rec-gate-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(basePath);
        await File.WriteAllBytesAsync(Path.Combine(basePath, "rec.webm"), [1, 2, 3]);

        await using var db = CreateCleanupDb();
        var session = NewSession(Guid.NewGuid());
        db.RemoteSessions.Add(session);
        db.RemoteSessionRecordings.Add(NewRecording(
            session.Id, RecordingStorageProvider.Local, "rec.webm", DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        var removed = await BuildCleanup(db, basePath).CleanupExpiredAsync(DateTime.UtcNow);

        Assert.That(removed, Is.EqualTo(1), "sem configuração o default true preserva o comportamento");

        Directory.Delete(basePath, recursive: true);
    }

    // ── RemoteAccessRecordingEnabled ──────────────────────────────

    [Test]
    public async Task Gravacao_desligada_no_escopo_nega_o_inicio()
    {
        await using var db = CreateRecordingDb();
        var (session, _) = await SeedRecordingScenarioAsync(db, serverValue: false);

        var service = BuildRecordingService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.StartRecordingAsync(session.Id, Guid.NewGuid()));
        Assert.That(ex!.Message, Does.Contain("disabled"));
    }

    [Test]
    public async Task Gravacao_ligada_permite_o_inicio()
    {
        await using var db = CreateRecordingDb();
        var (session, _) = await SeedRecordingScenarioAsync(db, serverValue: true);

        var service = BuildRecordingService(db);

        var recording = await service.StartRecordingAsync(session.Id, Guid.NewGuid());
        Assert.That(recording, Is.Not.Null);
        Assert.That(recording.Status, Is.EqualTo("recording"));
    }

    [Test]
    public async Task Gravacao_sem_resolvedor_mantem_o_comportamento_atual()
    {
        // Construção direta sem IConfigurationResolver (como os testes antigos):
        // não pode passar a bloquear gravação por causa do gate novo.
        await using var db = CreateRecordingDb();
        var (session, _) = await SeedRecordingScenarioAsync(db, serverValue: false);

        var service = new RemoteRecordingService(
            new RemoteSessionRepository(db), new NoopTerminalRecordingService(),
            Options.Create(new RemoteAccessOptions()), NullLogger<RemoteRecordingService>.Instance);

        var recording = await service.StartRecordingAsync(session.Id, Guid.NewGuid());
        Assert.That(recording, Is.Not.Null, "sem resolvedor vale o default true");
    }

    // ── PushEnabled: as DUAS camadas de gate ──────────────────────

    [Test]
    public void Push_a_fila_e_sempre_registrada_mas_o_worker_obedece_ao_toggle_de_boot()
    {
        // Camada 1 (BOOT): BackgroundJobs:WebPushDispatch:Enabled. A FILA precisa
        // ser sempre registrada (NotificationService depende dela) e apenas o
        // hosted service e condicionado — condicionar a fila quebraria o DI.
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundJobs:WebPushDispatch:Enabled"] = "false",
            })
            .Build();

        services.AddDiscoveryBackgroundServices(
            Discovery.Api.DependencyInjection.BackgroundServicesCollectionExtensions
                .ReadBackgroundServicesConfig(config, isDevelopment: false));

        Assert.Multiple(() =>
        {
            Assert.That(
                services.Any(sd => sd.ServiceType == typeof(Discovery.Core.Interfaces.IWebPushDispatchQueue)),
                Is.True,
                "a fila é sempre registrada (NotificationService depende dela)");
            Assert.That(
                services.Count(sd => sd.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                                     sd.ImplementationFactory is not null),
                Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public void Push_toggle_de_boot_reflete_o_appsettings()
    {
        static Discovery.Api.DependencyInjection.BackgroundServicesCollectionExtensions.BackgroundServicesConfig Read(string value)
            => Discovery.Api.DependencyInjection.BackgroundServicesCollectionExtensions.ReadBackgroundServicesConfig(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["BackgroundJobs:WebPushDispatch:Enabled"] = value,
                    })
                    .Build(),
                isDevelopment: false);

        Assert.Multiple(() =>
        {
            Assert.That(Read("false").WebPushDispatchEnabled, Is.False);
            Assert.That(Read("true").WebPushDispatchEnabled, Is.True);
        });
    }

    // ── RemoteAccessEnabled: valor que o gate de CRIAÇÃO consulta ──

    [Test]
    public async Task RemoteAccess_desligado_resolve_false_no_escopo_do_site()
    {
        // O handler de criação de sessão (StartRemoteSessionCommandHandler) chama
        // GetEffectiveValueAsync<bool>("Site", "RemoteAccessEnabled", siteId) e nega
        // quando o resultado é false. Este teste fixa exatamente esse contrato.
        await using var db = CreateRecordingDb();
        var (_, siteId, _) = await SeedRemoteAccessScenarioAsync(db, remoteAccessEnabled: false);

        var effective = await BuildRecordingResolver(db)
            .GetEffectiveValueAsync<bool>("Site", "RemoteAccessEnabled", siteId);

        Assert.That(effective, Is.False, "o gate do handler precisa enxergar false");
    }

    [Test]
    public async Task RemoteAccess_ligado_resolve_true_no_escopo_do_site()
    {
        await using var db = CreateRecordingDb();
        var (_, siteId, _) = await SeedRemoteAccessScenarioAsync(db, remoteAccessEnabled: true);

        var effective = await BuildRecordingResolver(db)
            .GetEffectiveValueAsync<bool>("Site", "RemoteAccessEnabled", siteId);

        Assert.That(effective, Is.True, "ligado não pode ser barrado pelo gate");
    }

    [Test]
    public async Task RemoteAccess_override_de_site_vence_o_servidor()
    {
        // Herança: o site desliga mesmo com o servidor ligado.
        await using var db = CreateRecordingDb();
        var (_, siteId, _) = await SeedRemoteAccessScenarioAsync(db, remoteAccessEnabled: true);

        var site = await db.SiteConfigurations.SingleAsync(s => s.SiteId == siteId);
        site.RemoteAccessEnabled = false;
        await db.SaveChangesAsync();

        var effective = await BuildRecordingResolver(db)
            .GetEffectiveValueAsync<bool>("Site", "RemoteAccessEnabled", siteId);

        Assert.That(effective, Is.False, "site vence o servidor");
    }

    [Test]
    public async Task RemoteAccess_sem_configuracao_cai_para_o_default_do_servidor()
    {
        // Sem linha de servidor o campo assume o default da entidade (true), que é
        // o comportamento de hoje — nada muda para quem só usa o appsettings.
        await using var db = CreateRecordingDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site sem config" });
        await db.SaveChangesAsync();

        var effective = await BuildRecordingResolver(db)
            .GetEffectiveValueAsync<bool>("Site", "RemoteAccessEnabled", siteId);

        Assert.That(effective, Is.True, "default true preserva o comportamento atual");
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static async Task<(Guid ClientId, Guid SiteId, Guid AgentId)> SeedRemoteAccessScenarioAsync(
        DiscoveryDbContext db, bool remoteAccessEnabled)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            RemoteAccessEnabled = remoteAccessEnabled,
        });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site RA" });
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(), SiteId = siteId, ClientId = clientId,
        });
        db.Agents.Add(new Agent
        {
            Id = agentId, SiteId = siteId, Hostname = "qa-ra-gate",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return (clientId, siteId, agentId);
    }

    /// <summary>Resolver real ligado ao banco de teste (mesmo harness dos demais testes).</summary>
    private static ConfigurationResolver BuildRecordingResolver(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        return new ConfigurationResolver(
            new ServerConfigurationRepository(db, redis, new ConfigurationBuilder().Build()),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            redis,
            new NoopSecretProtector());
    }

    private static RemoteSession NewSession(Guid agentId) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Status = "closed",
        StartedAt = DateTime.UtcNow.AddHours(-1),
        ExpiresAt = DateTime.UtcNow.AddHours(-1),
    };

    private static RemoteSessionRecording NewRecording(
        Guid sessionId, RecordingStorageProvider provider, string storageUrl, DateTime? expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        RemoteSessionId = sessionId,
        StorageProvider = provider,
        StorageUrl = storageUrl,
        Status = "completed",
        StartedAt = DateTime.UtcNow.AddHours(-1),
        RetentionExpiresAt = expiresAt,
    };

    private static IOptions<RemoteAccessOptions> BuildOptions(string localBasePath) =>
        Options.Create(new RemoteAccessOptions
        {
            Recording = new RemoteAccessRecordingOptions
            {
                Local = new RemoteAccessRecordingLocalOptions { BasePath = localBasePath },
            },
        });

    private static RecordingStorageCleanupService BuildCleanup(DiscoveryDbContext db, string basePath)
        => new(db, BuildOptions(basePath), new NoopHttpClientFactory(),
            NullLogger<RecordingStorageCleanupService>.Instance);

    private static async Task<(RemoteSession Session, Guid SiteId)> SeedRecordingScenarioAsync(
        DiscoveryDbContext db, bool serverValue)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            RemoteAccessRecordingEnabled = serverValue,
        });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Gravação" });
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(), SiteId = siteId, ClientId = clientId,
        });

        var agentId = Guid.NewGuid();
        db.Agents.Add(new Agent
        {
            Id = agentId, SiteId = siteId, Hostname = "qa-rec-gate",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var session = new RemoteSession
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            UserId = Guid.NewGuid(),
            TenantId = clientId,
            SiteId = siteId,
            Status = "active",
            StartedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        };
        db.RemoteSessions.Add(session);

        await db.SaveChangesAsync();
        return (session, siteId);
    }

    private static RemoteRecordingService BuildRecordingService(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        var resolver = new ConfigurationResolver(
            new ServerConfigurationRepository(db, redis, new ConfigurationBuilder().Build()),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            redis,
            new NoopSecretProtector());

        return new RemoteRecordingService(
            new RemoteSessionRepository(db), new NoopTerminalRecordingService(),
            Options.Create(new RemoteAccessOptions()), NullLogger<RemoteRecordingService>.Instance,
            resolver);
    }

    private static DiscoveryDbContext CreateCleanupDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"cfg-a-cleanup-{Guid.NewGuid():N}")
            .Options;
        return new GatesTestDbContext(options);
    }

    private static DiscoveryDbContext CreateRecordingDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"cfg-a-recording-{Guid.NewGuid():N}")
            .Options;
        return new GatesTestDbContext(options);
    }

    /// <summary>
    /// Modelo reduzido para os testes de GRAVAÇÃO: as navegações que apontam para
    /// entidades fora do modelo são ignoradas, senão o EF não consegue determinar
    /// o lado dependente do one-to-one RemoteSession.Recording.
    /// </summary>
    private sealed class GatesTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(ServerConfiguration), typeof(ClientConfiguration), typeof(SiteConfiguration),
                typeof(Client), typeof(Site), typeof(Agent),
                typeof(RemoteSession), typeof(RemoteSessionRecording),
                typeof(RemoteSessionAudit), typeof(RemoteTerminalRecording),
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<RemoteSession>(entity =>
            {
                entity.HasKey(item => item.Id);
                // Agent/Audits ficam fora do modelo reduzido.
                entity.Ignore(item => item.Agent);
                entity.Ignore(item => item.Audits);
                // Recording NÃO é ignorada: RemoteSessionRepository.GetByIdAsync faz
                // Include(s => s.Recording). Ignorar a navegação quebra o Include
                // ("The expression 's.Recording' is invalid inside an 'Include'").
                entity.HasOne(item => item.Recording)
                    .WithOne(recording => recording.RemoteSession!)
                    .HasForeignKey<RemoteSessionRecording>(recording => recording.RemoteSessionId);
            });
            modelBuilder.Entity<RemoteSessionRecording>(entity =>
            {
                entity.HasKey(item => item.Id);
            });
            modelBuilder.Entity<RemoteTerminalRecording>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Ignore(item => item.RemoteSession);
            });
            modelBuilder.Entity<RemoteSessionAudit>(entity => entity.HasKey(item => item.Id));
        }
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NoopTerminalRecordingService : ITerminalRecordingService
    {
        public Task<TerminalRecordingDto?> GetAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<TerminalRecordingDto?>(null);

        public Task<TerminalRecordingTenant?> GetTenantAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<TerminalRecordingTenant?>(null);

        public Task<TerminalRecordingDownloadDto?> OpenDownloadAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<TerminalRecordingDownloadDto?>(null);

        public Task<bool> DeleteAsync(Guid agentId, Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task FinalizeAsync(Guid sessionId, string status, string? lastError = null, CancellationToken ct = default)
            => Task.CompletedTask;
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
        public bool IsProtected(string? value) => false;
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
        public string UnprotectOrSelf(string? value) => value ?? string.Empty;
    }
}

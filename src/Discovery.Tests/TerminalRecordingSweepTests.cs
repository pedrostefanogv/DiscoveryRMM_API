using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services.Remote.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;


/// <summary>
/// Varredura de gravações inativas: sessão encerrada/expirada sem frame de saída
/// precisa ser finalizada (senão o estado em memória, o writer e a linha do banco
/// ficariam presos até o restart da API).
/// </summary>
[TestFixture]
public class TerminalRecordingSweepTests
{
    private string _basePath = null!;

    [SetUp]
    public void SetUp()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"terminal-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_basePath);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_basePath))
                Directory.Delete(_basePath, recursive: true);
        }
        catch (IOException)
        {
            // Handle do arquivo pode demorar alguns ms para fechar no Windows.
        }
    }

    private static TerminalRecordingFrameDto Frame()
        => new("hello", 1, 1_000, "output", null, null, null, null, null, true);

    private static string RawJson()
        => """{"data":"hello","seq":1,"timestampMs":1000,"kind":"output"}""";

    private static RemoteAccessOptions Options() => new()
    {
        Recording = new RemoteAccessRecordingOptions
        {
            Local = new RemoteAccessRecordingLocalOptions { BasePath = string.Empty },
            Terminal = new RemoteAccessTerminalRecordingOptions
            {
                FlushIntervalMs = 60_000,
                MaxFramesPerSecond = 0,
                MaxBytesPerSecond = 0,
                MaxRecordingBytes = 0
            }
        }
    };

    private sealed class Harness : IAsyncDisposable
    {
        public required DiscoveryDbContext Db { get; init; }
        public required RemoteSession Session { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required TerminalRecordingCoordinator Coordinator { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Provider.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private async Task<Harness> BuildAsync()
    {
        var options = Options();
        options.Recording.Local.BasePath = _basePath;

        var dbOptions = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"terminal-sweep-{Guid.NewGuid():N}")
            .Options;

        var db = new SweepTestDbContext(dbOptions);
        var session = new RemoteSession
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            SiteId = Guid.NewGuid(),
            Status = "active",
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        db.RemoteSessions.Add(session);
        await db.SaveChangesAsync();

        var monitor = new TestOptionsMonitor<RemoteAccessOptions>(options);
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<RemoteAccessOptions>>(monitor);
        services.AddScoped<DiscoveryDbContext>(_ => new SweepTestDbContext(dbOptions));
        var provider = services.BuildServiceProvider(validateScopes: true);

        var coordinator = new TerminalRecordingCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(), monitor,
            NullLogger<TerminalRecordingCoordinator>.Instance);

        return new Harness { Db = db, Session = session, Provider = provider, Coordinator = coordinator };
    }

    [Test]
    public async Task Sweep_finalizes_recording_when_session_was_closed_without_exit_frame()
    {
        await using var harness = await BuildAsync();
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());
        Assert.That(harness.Coordinator.ActiveRecordings, Is.EqualTo(1));

        // Sessão encerrada pelo servidor sem frame de saída nem StopRecording
        // (agent caiu): é o cenário que deixava estado e writer presos.
        session.Status = "ended";
        await harness.Db.SaveChangesAsync();

        var finalized = await harness.Coordinator.SweepInactiveAsync(TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(finalized, Is.EqualTo(1));
            Assert.That(harness.Coordinator.ActiveRecordings, Is.EqualTo(0), "o estado em memória precisa sair");
        });

        harness.Db.ChangeTracker.Clear();
        var row = await harness.Db.RemoteTerminalRecordings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RemoteSessionId == session.Id);
        Assert.That(row, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(row!.Status, Is.EqualTo(RemoteTerminalRecordingStatus.Completed));
            Assert.That(row.LastError, Does.Contain("varredura"));
            Assert.That(row.CompletedAt, Is.Not.Null);
            Assert.That(row.RetentionExpiresAt, Is.Not.Null, "sem retenção a linha nunca expiraria");
        });

        // Idempotente: nada mais a finalizar.
        Assert.That(await harness.Coordinator.SweepInactiveAsync(TimeSpan.Zero), Is.EqualTo(0));
    }

    [Test]
    public async Task Sweep_finalizes_recording_when_session_expired()
    {
        await using var harness = await BuildAsync();
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());

        session.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await harness.Db.SaveChangesAsync();

        Assert.That(await harness.Coordinator.SweepInactiveAsync(TimeSpan.Zero), Is.EqualTo(1));
        Assert.That(harness.Coordinator.ActiveRecordings, Is.EqualTo(0));
    }

    [Test]
    public async Task Sweep_keeps_recording_of_active_session_even_when_idle()
    {
        await using var harness = await BuildAsync();
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());

        // Terminal sem output por muito tempo é normal: a sessão segue ativa.
        var finalized = await harness.Coordinator.SweepInactiveAsync(TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(finalized, Is.EqualTo(0));
            Assert.That(harness.Coordinator.ActiveRecordings, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Sweep_skips_states_within_the_inactivity_grace()
    {
        await using var harness = await BuildAsync();
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());
        session.Status = "ended";
        await harness.Db.SaveChangesAsync();

        // Frame recente: nem é candidato (evita consultar o banco a cada flush).
        Assert.That(await harness.Coordinator.SweepInactiveAsync(TimeSpan.FromHours(1)), Is.EqualTo(0));
        Assert.That(harness.Coordinator.ActiveRecordings, Is.EqualTo(1));
    }

    /// <summary>Modelo reduzido: só as entidades do fluxo de terminal.</summary>
    private sealed class SweepTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(RemoteSession),
                typeof(RemoteSessionRecording),
                typeof(RemoteSessionAudit),
                typeof(RemoteTerminalRecording)
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
                entity.Ignore(item => item.Agent);
                entity.Ignore(item => item.Recording);
                entity.Ignore(item => item.Audits);
            });
            modelBuilder.Entity<RemoteSessionRecording>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Ignore(item => item.RemoteSession);
            });
            modelBuilder.Entity<RemoteTerminalRecording>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Ignore(item => item.RemoteSession);
            });
            modelBuilder.Entity<RemoteSessionAudit>(entity => entity.HasKey(item => item.Id));
        }
    }
}

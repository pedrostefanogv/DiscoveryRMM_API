using System.Threading.Channels;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Services.Remote.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

/// <summary>
/// Coordenador da gravação de terminal: flush do NDJSON, frames de controle vs
/// rate limit, teto de bytes e corrida entre finalize e criação de estado.
/// </summary>
[TestFixture]
public class TerminalRecordingCoordinatorTests
{
    private string _basePath = null!;

    [SetUp]
    public void SetUp()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"terminal-coord-{Guid.NewGuid():N}");
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

    private static TerminalRecordingFrameDto Frame(string kind = "output", string data = "hello")
        => new(data, 1, 1_000, kind, null, null, null, null, null, true);

    private static string RawJson(string kind = "output", string data = "hello")
        => $$"""{"data":"{{data}}","seq":1,"timestampMs":1000,"kind":"{{kind}}"}""";

    private RemoteAccessOptions Options(RemoteAccessTerminalRecordingOptions terminal) => new()
    {
        Recording = new RemoteAccessRecordingOptions
        {
            Local = new RemoteAccessRecordingLocalOptions { BasePath = _basePath },
            Terminal = terminal
        }
    };

    private string NdjsonPath(Guid sessionId) => Path.Combine(_basePath, "terminal", $"{sessionId:N}.ndjson");

    /// <summary>Lê o NDJSON com o arquivo ainda aberto pelo writer.</summary>
    private static string ReadTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

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

    /// <summary>
    /// Monta o coordenador com um DbContext in-memory compartilhado entre os
    /// escopos que ele mesmo cria (é o único jeito de exercitar o fluxo real:
    /// GetOrCreateState -> writer -> persist).
    /// </summary>
    private async Task<Harness> BuildAsync(
        RemoteAccessOptions options,
        Func<IServiceScopeFactory, IServiceScopeFactory>? wrapScopeFactory = null)
    {
        var dbOptions = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"terminal-coord-{Guid.NewGuid():N}")
            .Options;

        var db = new TerminalRecordingTestDbContext(dbOptions);
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
        services.AddScoped<DiscoveryDbContext>(_ => new TerminalRecordingTestDbContext(dbOptions));
        var provider = services.BuildServiceProvider(validateScopes: true);

        IServiceScopeFactory factory = provider.GetRequiredService<IServiceScopeFactory>();
        if (wrapScopeFactory is not null)
            factory = wrapScopeFactory(factory);

        var coordinator = new TerminalRecordingCoordinator(
            factory, monitor, NullLogger<TerminalRecordingCoordinator>.Instance);

        return new Harness { Db = db, Session = session, Provider = provider, Coordinator = coordinator };
    }

    [Test]
    public async Task Flush_persists_buffered_ndjson_before_finalize()
    {
        // Intervalo de flush alto: nada sai do buffer do writer sem o flush periódico.
        await using var harness = await BuildAsync(Options(new RemoteAccessTerminalRecordingOptions
        {
            FlushIntervalMs = 60_000,
            MaxFramesPerSecond = 0,
            MaxBytesPerSecond = 0,
            MaxRecordingBytes = 0
        }));
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());

        Assert.That(File.Exists(NdjsonPath(session.Id)), Is.True, "o writer abre o arquivo no primeiro append");
        await harness.Coordinator.FlushAsync();

        Assert.That(
            ReadTextShared(NdjsonPath(session.Id)),
            Does.Contain("\"data\":\"hello\""),
            "o flush periódico precisa drenar o buffer do writer (o download lê o arquivo em disco)");
    }

    [Test]
    public async Task Recording_bytes_cap_finalizes_as_failed_and_releases_state()
    {
        await using var harness = await BuildAsync(Options(new RemoteAccessTerminalRecordingOptions
        {
            FlushIntervalMs = 60_000,
            MaxFramesPerSecond = 0,
            MaxBytesPerSecond = 0,
            MaxRecordingBytes = 40
        }));
        var session = harness.Session;

        // Frame maior que o teto total: além de recusado, encerra a gravação.
        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(data: new string('x', 200)), RawJson(data: new string('x', 200)));

        Assert.That(harness.Coordinator.ActiveRecordings, Is.EqualTo(0),
            "a sessão não pode ficar presa em memória recebendo frames que sempre serão descartados");

        var row = await harness.Db.RemoteTerminalRecordings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RemoteSessionId == session.Id);
        Assert.That(row, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(row!.Status, Is.EqualTo(RemoteTerminalRecordingStatus.Failed));
            Assert.That(row.LastError, Does.Contain("limite"));
            Assert.That(row.Frames, Is.EqualTo(0));
        });

        // Frames seguintes da sessão também não reabrem a gravação.
        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());
        Assert.That(harness.Coordinator.ActiveRecordings, Is.EqualTo(0));
    }

    [Test]
    public async Task Exit_frame_is_not_dropped_by_frames_per_second_limit()
    {
        await using var harness = await BuildAsync(Options(new RemoteAccessTerminalRecordingOptions
        {
            FlushIntervalMs = 60_000,
            MaxFramesPerSecond = 1,
            MaxBytesPerSecond = 0,
            MaxRecordingBytes = 0
        }));
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());

        // Segundo frame na mesma janela seria recusado; por ser "exit", precisa passar.
        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame("exit", string.Empty), RawJson("exit", string.Empty));

        Assert.That(harness.Coordinator.ActiveRecordings, Is.EqualTo(0), "o exit encerra a gravação");
        var row = await harness.Db.RemoteTerminalRecordings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RemoteSessionId == session.Id);
        Assert.That(row!.Status, Is.EqualTo(RemoteTerminalRecordingStatus.Completed));
        Assert.That(ReadLinesShared(NdjsonPath(session.Id)), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task Finalize_concurrent_with_first_frame_does_not_leave_orphan_state()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var harness = await BuildAsync(
            Options(new RemoteAccessTerminalRecordingOptions
            {
                FlushIntervalMs = 60_000,
                MaxFramesPerSecond = 0,
                MaxBytesPerSecond = 0,
                MaxRecordingBytes = 0
            }),
            inner => new BlockingScopeFactory(inner, entered, release.Task));

        var session = harness.Session;

        // A linha fica "recording" sem estado em memória (caso de restart da API):
        // o StopRecording finaliza direto no banco enquanto um frame tardio chega.
        harness.Db.RemoteTerminalRecordings.Add(new RemoteTerminalRecording
        {
            Id = Guid.NewGuid(),
            RemoteSessionId = session.Id,
            AgentId = session.AgentId,
            ClientId = session.TenantId,
            Status = RemoteTerminalRecordingStatus.Recording,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            LastFrameAt = DateTime.UtcNow.AddMinutes(-5),
            StorageProvider = RecordingStorageProvider.Local,
            StorageUrl = TerminalRecordingStoragePaths.ResolveRelativePath(session.Id),
            RetentionExpiresAt = DateTime.UtcNow.AddDays(30)
        });
        await harness.Db.SaveChangesAsync();

        var finalize = Task.Run(() => harness.Coordinator.FinalizeAsync(
            session.Id, RemoteTerminalRecordingStatus.Completed));

        // Espera o finalize entrar no gate de criação (antes de tocar o banco).
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var frame = Task.Run(() => harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson()));

        await Task.Delay(150);
        release.TrySetResult();
        await Task.WhenAll(finalize, frame).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(harness.Coordinator.ActiveRecordings, Is.EqualTo(0),
            "um frame tardio não pode recriar o estado de uma gravação já finalizada");

        harness.Db.ChangeTracker.Clear();
        var row = await harness.Db.RemoteTerminalRecordings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RemoteSessionId == session.Id);
        Assert.That(row!.Status, Is.EqualTo(RemoteTerminalRecordingStatus.Completed));
    }

    [Test]
    public async Task Drain_reader_processes_pending_items_and_survives_failures()
    {
        var channel = Channel.CreateBounded<int>(10);
        for (var i = 1; i <= 3; i++)
            channel.Writer.TryWrite(i);
        channel.Writer.TryComplete();

        var handled = new List<int>();
        var (processed, failed) = await TerminalRecordingIngestService.DrainReaderAsync<int>(
            channel.Reader,
            (item, _) =>
            {
                if (item == 2)
                    throw new InvalidOperationException("frame inválido");
                handled.Add(item);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.EqualTo(new[] { 1, 3 }), "o erro de um item não interrompe o dreno");
            Assert.That(processed, Is.EqualTo(2));
            Assert.That(failed, Is.EqualTo(1));
            Assert.That(channel.Reader.Count, Is.EqualTo(0), "a fila fica vazia (nada é perdido no shutdown)");
        });
    }

    private sealed class BlockingScopeFactory(
        IServiceScopeFactory inner,
        TaskCompletionSource entered,
        Task release) : IServiceScopeFactory
    {
        private int _calls;

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            }

            return inner.CreateScope();
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>Modelo reduzido: só as entidades do fluxo de terminal.</summary>
    private sealed class TerminalRecordingTestDbContext(DbContextOptions<DiscoveryDbContext> options)
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

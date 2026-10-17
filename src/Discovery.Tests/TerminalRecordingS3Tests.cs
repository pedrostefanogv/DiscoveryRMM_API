using System.Net;
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

namespace Discovery.Tests;

/// <summary>
/// Gravação de terminal com StorageProvider=S3: o NDJSON continua sendo escrito
/// em disco durante a gravação e é enviado ao S3 no finalize (decisão do
/// usuário: gravar local E enviar ao S3 quando configurado). Cobre upload,
/// falha de upload (preserva o local), remoção na retenção/purge e o download
/// por URL pré-assinada (302) versus stream local.
/// </summary>
[TestFixture]
public class TerminalRecordingS3Tests
{
    private string _basePath = null!;

    [SetUp]
    public void SetUp()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"terminal-s3-{Guid.NewGuid():N}");
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
            // No Windows o handle do arquivo pode demorar alguns ms para fechar.
        }
    }

    private sealed record CapturedRequest(string Method, string Url, string? Body, string? ContentType);

    /// <summary>S3 fake: captura método/URL/corpo de cada request.</summary>
    private sealed class FakeS3Handler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public HttpStatusCode PutStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NoContent;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                body,
                request.Content?.Headers.ContentType?.MediaType));

            var status = request.Method == HttpMethod.Delete ? DeleteStatus : PutStatus;
            return new HttpResponseMessage(status);
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static TerminalRecordingFrameDto Frame()
        => new("hello", 1, 1_000, "output", null, null, null, null, null, true);

    private static string RawJson()
        => """{"data":"hello","seq":1,"timestampMs":1000,"kind":"output"}""";

    private RemoteAccessOptions BuildOptions(bool s3, int uploadMaxAttempts = 3, bool keepLocalCopy = true) => new()
    {
        Recording = new RemoteAccessRecordingOptions
        {
            StorageProvider = s3 ? "S3" : "Local",
            Local = new RemoteAccessRecordingLocalOptions { BasePath = _basePath },
            S3 = new RemoteAccessRecordingS3Options
            {
                Endpoint = "https://s3.example.test",
                Bucket = "recordings",
                UsePathStyle = true,
                PresignTtlMinutes = 15
            },
            Terminal = new RemoteAccessTerminalRecordingOptions
            {
                FlushIntervalMs = 60_000,
                MaxFramesPerSecond = 0,
                MaxBytesPerSecond = 0,
                MaxRecordingBytes = 0,
                UploadToS3 = true,
                KeepLocalCopyAfterUpload = keepLocalCopy,
                UploadTimeoutSeconds = 5,
                UploadMaxAttempts = uploadMaxAttempts
            }
        }
    };

    private sealed class Harness : IAsyncDisposable
    {
        public required string BasePath { get; init; }
        public required DiscoveryDbContext Db { get; init; }
        public required RemoteSession Session { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required TerminalRecordingCoordinator Coordinator { get; init; }
        public required FakeS3Handler Handler { get; init; }
        public required RemoteAccessOptions AccessOptions { get; init; }

        public TerminalRecordingService CreateService(DiscoveryDbContext db)
            => new(
                db,
                Options.Create(AccessOptions),
                Coordinator,
                Provider.GetRequiredService<IHttpClientFactory>(),
                NullLogger<TerminalRecordingService>.Instance);

        public string LocalPath(Guid sessionId)
            => Path.Combine(BasePath, "terminal", $"{sessionId:N}.ndjson");

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Provider.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private async Task<Harness> BuildAsync(
        bool s3 = true,
        int uploadMaxAttempts = 3,
        HttpStatusCode putStatus = HttpStatusCode.OK,
        bool keepLocalCopy = true)
    {
        var options = BuildOptions(s3, uploadMaxAttempts, keepLocalCopy);
        var handler = new FakeS3Handler { PutStatus = putStatus };

        var dbOptions = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"terminal-s3-{Guid.NewGuid():N}")
            .Options;

        var db = new S3TestDbContext(dbOptions);
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
        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
        services.AddScoped<DiscoveryDbContext>(_ => new S3TestDbContext(dbOptions));
        var provider = services.BuildServiceProvider(validateScopes: true);

        var coordinator = new TerminalRecordingCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            monitor,
            NullLogger<TerminalRecordingCoordinator>.Instance);

        return new Harness
        {
            BasePath = _basePath,
            Db = db,
            Session = session,
            Provider = provider,
            Coordinator = coordinator,
            Handler = handler,
            AccessOptions = options
        };
    }

    [Test]
    public async Task Finalize_uploads_ndjson_to_s3_with_correct_content()
    {
        await using var harness = await BuildAsync();
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());

        // Durante a gravação o conteúdo está em disco (o provider ainda é Local).
        Assert.That(File.Exists(harness.LocalPath(session.Id)), Is.True);
        Assert.That(harness.Handler.Requests, Is.Empty, "nada deve ser enviado antes do finalize");

        await harness.Coordinator.FinalizeAsync(session.Id, RemoteTerminalRecordingStatus.Completed);

        var put = harness.Handler.Requests.Single(r => r.Method == "PUT");
        Assert.Multiple(() =>
        {
            Assert.That(put.Url, Is.EqualTo(
                $"https://s3.example.test/recordings/terminal/{session.Id:N}.ndjson"));
            Assert.That(put.ContentType, Is.EqualTo("application/x-ndjson"));
            Assert.That(put.Body, Does.Contain(session.Id.ToString("D")));
            Assert.That(put.Body, Does.Contain("hello"));
        });

        // KeepLocalCopyAfterUpload=true (default): a cópia local permanece.
        Assert.That(File.Exists(harness.LocalPath(session.Id)), Is.True);

        harness.Db.ChangeTracker.Clear();
        var row = await harness.Db.RemoteTerminalRecordings.AsNoTracking()
            .FirstAsync(r => r.RemoteSessionId == session.Id);

        Assert.Multiple(() =>
        {
            Assert.That(row.StorageProvider, Is.EqualTo(RecordingStorageProvider.S3));
            Assert.That(row.Status, Is.EqualTo(RemoteTerminalRecordingStatus.Completed));
            Assert.That(row.LastError, Is.Null);
        });
    }

    [Test]
    public async Task Finalize_upload_failure_keeps_local_file_and_records_last_error()
    {
        await using var harness = await BuildAsync(uploadMaxAttempts: 1, putStatus: HttpStatusCode.InternalServerError);
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());
        await harness.Coordinator.FinalizeAsync(session.Id, RemoteTerminalRecordingStatus.Completed);

        Assert.That(harness.Handler.Requests.Count(r => r.Method == "PUT"), Is.EqualTo(1));
        Assert.That(File.Exists(harness.LocalPath(session.Id)), Is.True, "falha de upload não pode apagar o NDJSON local");

        harness.Db.ChangeTracker.Clear();
        var row = await harness.Db.RemoteTerminalRecordings.AsNoTracking()
            .FirstAsync(r => r.RemoteSessionId == session.Id);

        Assert.Multiple(() =>
        {
            Assert.That(row.StorageProvider, Is.EqualTo(RecordingStorageProvider.Local),
                "sem upload confirmado a gravação continua local");
            Assert.That(row.Status, Is.EqualTo(RemoteTerminalRecordingStatus.Completed));
            Assert.That(row.LastError, Does.Contain("S3"));
        });
    }

    [Test]
    public async Task Finalize_removes_local_copy_after_successful_upload_when_keep_local_is_false()
    {
        await using var harness = await BuildAsync(keepLocalCopy: false);
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());
        Assert.That(File.Exists(harness.LocalPath(session.Id)), Is.True, "durante a gravação o arquivo é sempre local");

        await harness.Coordinator.FinalizeAsync(session.Id, RemoteTerminalRecordingStatus.Completed);

        Assert.That(harness.Handler.Requests.Count(r => r.Method == "PUT"), Is.EqualTo(1));
        Assert.That(File.Exists(harness.LocalPath(session.Id)), Is.False,
            "com KeepLocalCopyAfterUpload=false a cópia local sai só depois do upload confirmado");

        harness.Db.ChangeTracker.Clear();
        var row = await harness.Db.RemoteTerminalRecordings.AsNoTracking()
            .FirstAsync(r => r.RemoteSessionId == session.Id);
        Assert.That(row.StorageProvider, Is.EqualTo(RecordingStorageProvider.S3));
    }

    [Test]
    public async Task Finalize_retries_upload_until_max_attempts()
    {
        await using var harness = await BuildAsync(uploadMaxAttempts: 2, putStatus: HttpStatusCode.ServiceUnavailable);
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());
        await harness.Coordinator.FinalizeAsync(session.Id, RemoteTerminalRecordingStatus.Completed);

        Assert.That(harness.Handler.Requests.Count(r => r.Method == "PUT"), Is.EqualTo(2));
    }

    [Test]
    public async Task Local_mode_never_calls_s3()
    {
        await using var harness = await BuildAsync(s3: false);
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());
        await harness.Coordinator.FinalizeAsync(session.Id, RemoteTerminalRecordingStatus.Completed);

        Assert.That(harness.Handler.Requests, Is.Empty, "modo Local deve ser idêntico ao comportamento anterior");
        Assert.That(File.Exists(harness.LocalPath(session.Id)), Is.True);

        harness.Db.ChangeTracker.Clear();
        var row = await harness.Db.RemoteTerminalRecordings.AsNoTracking()
            .FirstAsync(r => r.RemoteSessionId == session.Id);
        Assert.That(row.StorageProvider, Is.EqualTo(RecordingStorageProvider.Local));
    }

    [Test]
    public async Task CleanupExpired_removes_terminal_s3_object_and_tolerates_missing_object()
    {
        await using var harness = await BuildAsync();
        var session = harness.Session;

        harness.Db.RemoteTerminalRecordings.Add(new RemoteTerminalRecording
        {
            Id = Guid.NewGuid(),
            RemoteSessionId = session.Id,
            AgentId = session.AgentId,
            ClientId = session.TenantId,
            Status = RemoteTerminalRecordingStatus.Completed,
            StartedAt = DateTime.UtcNow.AddDays(-10),
            CompletedAt = DateTime.UtcNow.AddDays(-10),
            StorageProvider = RecordingStorageProvider.S3,
            StorageUrl = $"{session.Id:N}.ndjson",
            RetentionExpiresAt = DateTime.UtcNow.AddDays(-1)
        });
        await harness.Db.SaveChangesAsync();

        // Objeto já não existe no bucket: 404 não pode interromper a retenção.
        harness.Handler.DeleteStatus = HttpStatusCode.NotFound;

        var cleanup = new RecordingStorageCleanupService(
            harness.Db,
            Options.Create(harness.AccessOptions),
            harness.Provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<RecordingStorageCleanupService>.Instance);

        var removed = await cleanup.CleanupExpiredAsync(DateTime.UtcNow);

        var delete = harness.Handler.Requests.Single(r => r.Method == "DELETE");
        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(delete.Url, Is.EqualTo(
                $"https://s3.example.test/recordings/terminal/{session.Id:N}.ndjson"));
        });
        Assert.That(await harness.Db.RemoteTerminalRecordings.CountAsync(), Is.EqualTo(0),
            "a linha expirada sai mesmo com o objeto ausente no S3");
    }

    [Test]
    public async Task DeleteForAgent_removes_terminal_s3_object()
    {
        await using var harness = await BuildAsync();
        var session = harness.Session;

        harness.Db.RemoteTerminalRecordings.Add(new RemoteTerminalRecording
        {
            Id = Guid.NewGuid(),
            RemoteSessionId = session.Id,
            AgentId = session.AgentId,
            ClientId = session.TenantId,
            Status = RemoteTerminalRecordingStatus.Completed,
            StartedAt = DateTime.UtcNow.AddDays(-1),
            CompletedAt = DateTime.UtcNow.AddDays(-1),
            StorageProvider = RecordingStorageProvider.S3,
            StorageUrl = $"{session.Id:N}.ndjson",
            RetentionExpiresAt = DateTime.UtcNow.AddDays(10)
        });
        await harness.Db.SaveChangesAsync();

        var cleanup = new RecordingStorageCleanupService(
            harness.Db,
            Options.Create(harness.AccessOptions),
            harness.Provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<RecordingStorageCleanupService>.Instance);

        await cleanup.DeleteForAgentAsync(session.AgentId);

        var delete = harness.Handler.Requests.Single(r => r.Method == "DELETE");
        Assert.That(delete.Url, Does.Contain($"terminal/{session.Id:N}.ndjson"));
    }

    [Test]
    public async Task Download_returns_s3_presigned_redirect_when_recording_is_in_s3()
    {
        await using var harness = await BuildAsync();
        var session = harness.Session;

        harness.Db.RemoteTerminalRecordings.Add(new RemoteTerminalRecording
        {
            Id = Guid.NewGuid(),
            RemoteSessionId = session.Id,
            AgentId = session.AgentId,
            ClientId = session.TenantId,
            Status = RemoteTerminalRecordingStatus.Completed,
            StartedAt = DateTime.UtcNow.AddHours(-1),
            CompletedAt = DateTime.UtcNow.AddHours(-1),
            StorageProvider = RecordingStorageProvider.S3,
            StorageUrl = $"{session.Id:N}.ndjson",
            RetentionExpiresAt = DateTime.UtcNow.AddDays(10)
        });
        await harness.Db.SaveChangesAsync();

        var service = harness.CreateService(harness.Db);
        var download = await service.OpenDownloadAsync(session.AgentId, session.Id);

        Assert.That(download, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(download!.Content, Is.Null, "no modo S3 o conteúdo não passa pela API");
            Assert.That(download.S3Url, Is.Not.Null.And.Contain($"terminal/{session.Id:N}.ndjson"));
            Assert.That(download.S3Url, Does.Contain("X-Amz-Expires="));

            // TTL curto: o presign usa no máximo 5 minutos (mesmo com
            // PresignTtlMinutes=15 configurado).
            var expires = long.Parse(download.S3Url!.Split("X-Amz-Expires=")[1]);
            var ttl = DateTimeOffset.FromUnixTimeSeconds(expires) - DateTimeOffset.UtcNow;
            Assert.That(ttl, Is.LessThanOrEqualTo(TimeSpan.FromMinutes(5)).And.GreaterThan(TimeSpan.Zero));
            Assert.That(download.S3Url, Does.StartWith("https://s3.example.test/recordings/"));
            Assert.That(download.FileName, Is.EqualTo($"terminal-{session.Id:N}.ndjson"));
        });
    }

    [Test]
    public async Task Download_streams_local_file_when_provider_is_local()
    {
        await using var harness = await BuildAsync(s3: false);
        var session = harness.Session;

        await harness.Coordinator.ProcessFrameAsync(
            session.Id, session.AgentId, session.TenantId, Frame(), RawJson());
        await harness.Coordinator.FinalizeAsync(session.Id, RemoteTerminalRecordingStatus.Completed);

        var service = harness.CreateService(harness.Db);
        var download = await service.OpenDownloadAsync(session.AgentId, session.Id);

        Assert.That(download, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(download!.S3Url, Is.Null);
            Assert.That(download.Content, Is.Not.Null);
        });

        await using var stream = download.Content!;
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        Assert.That(content, Does.Contain("hello"));
    }

    /// <summary>Modelo reduzido: só as entidades do fluxo de terminal.</summary>
    private sealed class S3TestDbContext(DbContextOptions<DiscoveryDbContext> options)
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

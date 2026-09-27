using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services.Remote.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

/// <summary>
/// Limpeza de storage das gravações: o purge do agente e a retenção devem
/// remover os arquivos (local/S3), não só as linhas do banco.
/// </summary>
public class RecordingStorageCleanupTests
{
    private static DiscoveryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"recording-cleanup-{Guid.NewGuid():N}")
            .Options;
        return new RecordingCleanupTestDbContext(options);
    }

    private static IOptions<RemoteAccessOptions> BuildOptions(string localBasePath) =>
        Options.Create(new RemoteAccessOptions
        {
            Recording = new RemoteAccessRecordingOptions
            {
                Local = new RemoteAccessRecordingLocalOptions { BasePath = localBasePath },
                S3 = new RemoteAccessRecordingS3Options
                {
                    Endpoint = "https://s3.example.test",
                    Bucket = "recordings",
                    UsePathStyle = true
                }
            }
        });

    private static RemoteSession NewSession(Guid agentId) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Status = "closed",
        StartedAt = DateTime.UtcNow.AddHours(-1),
        ExpiresAt = DateTime.UtcNow.AddHours(-1)
    };

    private static RemoteSessionRecording NewRecording(Guid sessionId, RecordingStorageProvider provider, string storageUrl, DateTime? expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        RemoteSessionId = sessionId,
        StorageProvider = provider,
        StorageUrl = storageUrl,
        Status = "completed",
        StartedAt = DateTime.UtcNow.AddHours(-1),
        RetentionExpiresAt = expiresAt
    };

    [Test]
    public async Task CleanupExpired_RemovesLocalFileAndRow()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"rec-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(basePath);
        var localFile = Path.Combine(basePath, "rec.webm");
        await File.WriteAllBytesAsync(localFile, [1, 2, 3]);

        using var db = CreateDbContext();
        var session = NewSession(Guid.NewGuid());
        db.RemoteSessions.Add(session);
        db.RemoteSessionRecordings.Add(NewRecording(session.Id, RecordingStorageProvider.Local, "rec.webm", DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        var service = new RecordingStorageCleanupService(
            db, BuildOptions(basePath), new StubHttpClientFactory(new RecordingHandler()), NullLogger<RecordingStorageCleanupService>.Instance);

        var removed = await service.CleanupExpiredAsync(DateTime.UtcNow);

        Assert.That(removed, Is.EqualTo(1));
        Assert.That(File.Exists(localFile), Is.False);
        Assert.That(await db.RemoteSessionRecordings.CountAsync(), Is.EqualTo(0));

        Directory.Delete(basePath, recursive: true);
    }

    [Test]
    public async Task DeleteForAgent_RemovesOnlyThatAgentsFiles()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"rec-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(basePath);
        var agentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var agentFile = Path.Combine(basePath, "agent.webm");
        var otherFile = Path.Combine(basePath, "other.webm");
        await File.WriteAllBytesAsync(agentFile, [1]);
        await File.WriteAllBytesAsync(otherFile, [2]);

        using var db = CreateDbContext();
        var session = NewSession(agentId);
        var otherSession = NewSession(otherAgentId);
        db.RemoteSessions.AddRange(session, otherSession);
        db.RemoteSessionRecordings.AddRange(
            NewRecording(session.Id, RecordingStorageProvider.Local, "agent.webm", null),
            NewRecording(otherSession.Id, RecordingStorageProvider.Local, "other.webm", null));
        await db.SaveChangesAsync();

        var service = new RecordingStorageCleanupService(
            db, BuildOptions(basePath), new StubHttpClientFactory(new RecordingHandler()), NullLogger<RecordingStorageCleanupService>.Instance);

        await service.DeleteForAgentAsync(agentId);

        Assert.That(File.Exists(agentFile), Is.False);
        Assert.That(File.Exists(otherFile), Is.True);
        // As linhas permanecem (o purge do agente as remove em seguida).
        Assert.That(await db.RemoteSessionRecordings.CountAsync(), Is.EqualTo(2));

        Directory.Delete(basePath, recursive: true);
    }

    [Test]
    public async Task DeleteForAgent_DeletesS3ObjectByUrl()
    {
        using var db = CreateDbContext();
        var agentId = Guid.NewGuid();
        var session = NewSession(agentId);
        db.RemoteSessions.Add(session);
        db.RemoteSessionRecordings.Add(NewRecording(
            session.Id,
            RecordingStorageProvider.S3,
            "https://s3.example.test/recordings/rec.webm",
            null));
        await db.SaveChangesAsync();

        var handler = new RecordingHandler();
        var service = new RecordingStorageCleanupService(
            db, BuildOptions(Path.GetTempPath()), new StubHttpClientFactory(handler), NullLogger<RecordingStorageCleanupService>.Instance);

        await service.DeleteForAgentAsync(agentId);

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(handler.Requests[0], Does.Contain("DELETE"));
        Assert.That(handler.Requests[0], Does.Contain("https://s3.example.test/recordings/rec.webm"));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingCleanupTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(RemoteSession),
                typeof(RemoteSessionRecording),
                typeof(RemoteSessionAudit)
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
                // Navegações ignoradas: o teste não as usa e evita validar
                // relacionamentos com entidades fora do modelo reduzido.
                entity.Ignore(item => item.Agent);
                entity.Ignore(item => item.Recording);
                entity.Ignore(item => item.Audits);
            });
            modelBuilder.Entity<RemoteSessionRecording>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Ignore(item => item.RemoteSession);
            });
            modelBuilder.Entity<RemoteSessionAudit>(entity => entity.HasKey(item => item.Id));
        }
    }
}

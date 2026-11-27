using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

[TestFixture]
public class ManagedFilesAndLinksTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"files-test-{Guid.NewGuid():N}")
            .Options;
        return new FilesTestDbContext(opts);
    }

    private sealed class FilesTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var keep = new HashSet<Type>
            {
                typeof(ManagedFile),
                typeof(FileShareLink),
                typeof(FileShareLinkItem),
                typeof(FileUploadLink),
                typeof(FileAccessLog),
                typeof(Attachment),
                typeof(Client),
                typeof(Site),
                typeof(Ticket),
                typeof(Discovery.Core.Entities.Identity.User),
            };

            foreach (var entityType in typeof(ManagedFile).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                             && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !keep.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<ManagedFile>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<FileShareLink>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<FileShareLinkItem>(e =>
            {
                e.HasKey(x => new { x.ShareLinkId, x.ManagedFileId });
                e.HasOne(x => x.ShareLink).WithMany(l => l.Items).HasForeignKey(x => x.ShareLinkId);
                e.HasOne(x => x.ManagedFile).WithMany().HasForeignKey(x => x.ManagedFileId);
            });
            modelBuilder.Entity<FileUploadLink>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<FileAccessLog>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<Attachment>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<Client>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<Site>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<Ticket>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<Discovery.Core.Entities.Identity.User>(e => e.HasKey(x => x.Id));
        }
    }

    [Test]
    public void FileValidationHelper_SanitizesReservedNamesAndTraversal()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FileValidationHelper.SanitizeFileName("../../evil/CON.txt"), Is.EqualTo("_CON.txt"));
            Assert.That(FileValidationHelper.NormalizeFolderPath("/../a/b/../c"), Is.EqualTo("/a/b/c"));
            Assert.That(FileValidationHelper.NormalizeExtensions(["pdf", ".PNG"]), Is.EqualTo(new[] { ".pdf", ".png" }));
        });

        var (ok1, _) = FileValidationHelper.ValidateFileType("doc.pdf", "application/pdf", [".pdf"], ["application/pdf"]);
        var (ok2, _) = FileValidationHelper.ValidateFileType("script.exe", "application/x-msdownload", [".pdf"], ["application/pdf"]);
        var (ok3, _) = FileValidationHelper.ValidateFileType("foto.png", "image/png", [".png"], ["image/*"]);

        Assert.Multiple(() =>
        {
            Assert.That(ok1, Is.True);
            Assert.That(ok2, Is.False);
            Assert.That(ok3, Is.True);
        });
    }

    [Test]
    public void AwsSigV4Signer_BuildContentDispositionAttachment_FormatsRfc5987()
    {
        var header = AwsSigV4Signer.BuildContentDispositionAttachment("relatório (final).pdf");
        Assert.That(header, Does.StartWith("attachment; filename=\""));
        Assert.That(header, Does.Contain("filename*=UTF-8''"));
        Assert.That(header, Does.Not.Contain("\r"));
        Assert.That(header, Does.Not.Contain("\n"));
    }

    [Test]
    public async Task ManagedFileService_ListForAgent_AppliesGlobalClientSiteInheritance()
    {
        await using var db = CreateDb();
        var clientA = Guid.NewGuid();
        var siteA1 = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var siteB1 = Guid.NewGuid();

        db.ManagedFiles.AddRange(
            MakeFile("global-public", FileScope.Global, null, null, FileVisibility.Public),
            MakeFile("global-private", FileScope.Global, null, null, FileVisibility.Private),
            MakeFile("clientA-agent", FileScope.Client, clientA, null, FileVisibility.AgentOnly),
            MakeFile("siteA1-agent", FileScope.Site, clientA, siteA1, FileVisibility.AgentOnly),
            MakeFile("clientB-agent", FileScope.Client, clientB, null, FileVisibility.AgentOnly),
            MakeFile("siteB1-agent", FileScope.Site, clientB, siteB1, FileVisibility.AgentOnly)
        );
        await db.SaveChangesAsync();

        var service = new ManagedFileService(db, new StubStorageFactory(new InMemoryStubStorage()), NullLogger<ManagedFileService>.Instance);
        var list = await service.ListForAgentAsync(clientA, siteA1);

        Assert.That(list.Select(f => f.Name), Is.EquivalentTo(new[] { "global-public", "clientA-agent", "siteA1-agent" }));
    }

    [Test]
    public async Task FileLinkService_ShareLink_EnforcesPasscodeLockoutAndMaxDownloads()
    {
        await using var db = CreateDb();
        var file = MakeFile("installer", FileScope.Global, null, null, FileVisibility.Private);
        db.ManagedFiles.Add(file);
        await db.SaveChangesAsync();

        var storage = new InMemoryStubStorage();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-secret-key-for-share-session-token-123456"
        }).Build();

        var linkService = new FileLinkService(db, new StubStorageFactory(storage), config, NullLogger<FileLinkService>.Instance);

        var created = await linkService.CreateShareLinkAsync(new CreateShareLinkRequest(
            Title: "Pacote Cliente",
            Message: "Baixe o arquivo",
            ClientId: null,
            SiteId: null,
            FileIds: [file.Id],
            ExpiresAtUtc: DateTime.UtcNow.AddDays(1),
            RequirePasscode: true,
            Passcode: "123456",
            MaxDownloads: 1), "admin");

        // Sem sessão: não libera lista de arquivos
        var infoLocked = await linkService.GetPublicShareInfoAsync(created.ShortCode, null);
        Assert.That(infoLocked.Unlocked, Is.False);
        Assert.That(infoLocked.Files, Is.Empty);

        // Senha errada
        var bad = await linkService.VerifySharePasscodeAsync(created.ShortCode, "000000", "1.2.3.4", "ua");
        Assert.That(bad.Valid, Is.False);

        // Senha correta
        var ok = await linkService.VerifySharePasscodeAsync(created.ShortCode, "123456", "1.2.3.4", "ua");
        Assert.That(ok.Valid, Is.True);
        Assert.That(ok.SessionToken, Is.Not.Null.And.Not.Empty);

        // 1º download: sucesso
        var dl1 = await linkService.DownloadShareFileAsync(created.ShortCode, file.Id, ok.SessionToken, "1.2.3.4", "ua");
        Assert.That(dl1.DownloadUrl, Does.Contain(file.StorageObjectKey));

        // 2º download: bloqueado por MaxDownloads = 1
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await linkService.DownloadShareFileAsync(created.ShortCode, file.Id, ok.SessionToken, "1.2.3.4", "ua"));

        // Lockout após 10 falhas seguidas
        var link2 = await linkService.CreateShareLinkAsync(new CreateShareLinkRequest(
            "Lockout Test", null, null, null, [file.Id], DateTime.UtcNow.AddDays(1), true, "secret", null), "admin");
        for (var i = 0; i < 10; i++)
        {
            await linkService.VerifySharePasscodeAsync(link2.ShortCode, "wrong", "1.2.3.4", "ua");
        }
        var lockedRes = await linkService.VerifySharePasscodeAsync(link2.ShortCode, "secret", "1.2.3.4", "ua");
        Assert.That(lockedRes.Valid, Is.False);
        Assert.That(lockedRes.Error, Does.Contain("Muitas tentativas"));
    }

    [Test]
    public async Task FileLinkService_UploadLink_DeletesOversizedObjectFromS3OnComplete()
    {
        await using var db = CreateDb();
        var storage = new InMemoryStubStorage { NextMetadataSizeBytes = 50_000_000 }; // 50 MB real no S3
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var linkService = new FileLinkService(db, new StubStorageFactory(storage), config, NullLogger<FileLinkService>.Instance);

        var uploadLink = await linkService.CreateUploadLinkAsync(new CreateUploadLinkRequest(
            Title: "Enviar Logs",
            Description: null,
            ClientId: null,
            SiteId: null,
            TargetFolderPath: "/logs",
            ExpiresAtUtc: DateTime.UtcNow.AddDays(1),
            RequirePasscode: false,
            Passcode: null,
            AllowMultiple: false,
            MaxFiles: 1,
            MaxFileSizeBytes: 5_000_000, // 5 MB max
            MaxTotalSizeBytes: 5_000_000,
            AllowedExtensions: [".zip"],
            AllowedMimeTypes: ["application/zip"],
            FileRetentionDays: 30,
            NotifyOnUpload: false), "admin");

        var init = await linkService.InitiatePublicUploadAsync(
            uploadLink.ShortCode,
            new InitiatePublicUploadRequest("logs.zip", "application/zip", 1_000_000),
            null);

        // Cliente burlou e subiu 50 MB no S3 -> Complete deve rejeitar e apagar do S3!
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await linkService.CompletePublicUploadAsync(
                uploadLink.ShortCode,
                new CompletePublicUploadRequest(init.FileId, init.ObjectKey, "logs.zip", "application/zip", 1_000_000),
                null, "1.2.3.4", "ua"));

        Assert.That(storage.DeletedKeys, Contains.Item(init.ObjectKey));
        Assert.That(await db.ManagedFiles.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task FileLinkService_TicketUploadLink_CreatesTicketAttachmentOnComplete()
    {
        await using var db = CreateDb();
        var ticketId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var storage = new InMemoryStubStorage { NextMetadataSizeBytes = 2048 };
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var linkService = new FileLinkService(db, new StubStorageFactory(storage), config, NullLogger<FileLinkService>.Instance);

        var link = await linkService.CreateUploadLinkAsync(new CreateUploadLinkRequest(
            Title: "Logs Máquina Externa",
            Description: null,
            ClientId: clientId,
            SiteId: null,
            TargetFolderPath: $"/chamados/{ticketId:N}",
            ExpiresAtUtc: DateTime.UtcNow.AddDays(1),
            RequirePasscode: false,
            Passcode: null,
            AllowMultiple: true,
            MaxFiles: 5,
            MaxFileSizeBytes: 10_000_000,
            MaxTotalSizeBytes: 20_000_000,
            AllowedExtensions: [".zip"],
            AllowedMimeTypes: ["application/zip"],
            FileRetentionDays: null,
            NotifyOnUpload: true,
            TicketId: ticketId), "tech");

        var init = await linkService.InitiatePublicUploadAsync(
            link.ShortCode,
            new InitiatePublicUploadRequest("diag.zip", "application/zip", 2048),
            null);

        var summary = await linkService.CompletePublicUploadAsync(
            link.ShortCode,
            new CompletePublicUploadRequest(init.FileId, init.ObjectKey, "diag.zip", "application/zip", 2048),
            null, "1.2.3.4", "ua");

        var attachment = await db.Attachments.FirstOrDefaultAsync(a => a.EntityId == ticketId);
        Assert.Multiple(() =>
        {
            Assert.That(summary.FileName, Is.EqualTo("diag.zip"));
            Assert.That(attachment, Is.Not.Null);
            Assert.That(attachment!.EntityType, Is.EqualTo("Ticket"));
            Assert.That(attachment.StorageObjectKey, Is.EqualTo(init.ObjectKey));
        });
    }

    [Test]
    public async Task FileLinkService_CompletePublicUpload_NotifiesCreatorAndTicketAssigneeWhenEnabled()
    {
        await using var db = CreateDb();
        var creator = new Discovery.Core.Entities.Identity.User
        {
            Id = Guid.NewGuid(),
            Login = "tech.creator",
            Email = "creator@example.com",
            FullName = "Tech Creator",
            IsActive = true
        };
        var assignee = new Discovery.Core.Entities.Identity.User
        {
            Id = Guid.NewGuid(),
            Login = "tech.assignee",
            Email = "assignee@example.com",
            FullName = "Tech Assignee",
            IsActive = true
        };
        var ticketId = Guid.NewGuid();
        db.Users.AddRange(creator, assignee);
        db.Tickets.Add(new Ticket
        {
            Id = ticketId,
            ClientId = Guid.NewGuid(),
            WorkflowStateId = Guid.NewGuid(),
            Title = "Impressora sem rede",
            AssignedToUserId = assignee.Id
        });
        await db.SaveChangesAsync();

        var storage = new InMemoryStubStorage { NextMetadataSizeBytes = 4096 };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-secret-for-unit-tests-123456"
        }).Build();
        var recorder = new RecordingNotificationService();
        var linkService = new FileLinkService(db, new StubStorageFactory(storage), cfg, NullLogger<FileLinkService>.Instance, recorder);

        var link = await linkService.CreateUploadLinkAsync(new CreateUploadLinkRequest(
            Title: "Logs da impressora",
            Description: null,
            ClientId: null,
            SiteId: null,
            TargetFolderPath: $"/chamados/{ticketId:N}",
            RequirePasscode: false,
            Passcode: null,
            ExpiresAtUtc: DateTime.UtcNow.AddDays(1),
            AllowMultiple: true,
            MaxFiles: 3,
            MaxFileSizeBytes: 1024 * 1024,
            MaxTotalSizeBytes: 3 * 1024 * 1024,
            AllowedExtensions: [".zip"],
            AllowedMimeTypes: ["application/zip"],
            FileRetentionDays: null,
            NotifyOnUpload: true,
            TicketId: ticketId), creator.Login);

        var init = await linkService.InitiatePublicUploadAsync(
            link.ShortCode,
            new InitiatePublicUploadRequest("printer.zip", "application/zip", 4096),
            null);

        await linkService.CompletePublicUploadAsync(
            link.ShortCode,
            new CompletePublicUploadRequest(init.FileId, init.ObjectKey, "printer.zip", "application/zip", 4096),
            null, "10.0.0.5", "ua");

        Assert.Multiple(() =>
        {
            Assert.That(recorder.Published, Has.Count.EqualTo(2));
            Assert.That(recorder.Published.Select(p => p.RecipientUserId), Is.EquivalentTo(new[] { creator.Id, assignee.Id }));
            Assert.That(recorder.Published.All(p => p.EventType == "file.upload.completed"), Is.True);
            Assert.That(recorder.Published.All(p => p.Topic == "tickets"), Is.True);
        });
    }

    [Test]
    public async Task FileLinkService_CompletePublicUpload_DoesNotNotifyWhenDisabled()
    {
        await using var db = CreateDb();
        var storage = new InMemoryStubStorage { NextMetadataSizeBytes = 1024 };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-secret-for-unit-tests-123456"
        }).Build();
        var recorder = new RecordingNotificationService();
        var linkService = new FileLinkService(db, new StubStorageFactory(storage), cfg, NullLogger<FileLinkService>.Instance, recorder);

        var link = await linkService.CreateUploadLinkAsync(new CreateUploadLinkRequest(
            Title: "Envio silencioso",
            Description: null,
            ClientId: null,
            SiteId: null,
            TargetFolderPath: "/recebidos",
            RequirePasscode: false,
            Passcode: null,
            ExpiresAtUtc: DateTime.UtcNow.AddDays(1),
            AllowMultiple: false,
            MaxFiles: 1,
            MaxFileSizeBytes: 1024 * 1024,
            MaxTotalSizeBytes: 1024 * 1024,
            AllowedExtensions: [".zip"],
            AllowedMimeTypes: ["application/zip"],
            FileRetentionDays: null,
            NotifyOnUpload: false), "tech");

        var init = await linkService.InitiatePublicUploadAsync(
            link.ShortCode,
            new InitiatePublicUploadRequest("silent.zip", "application/zip", 1024),
            null);

        await linkService.CompletePublicUploadAsync(
            link.ShortCode,
            new CompletePublicUploadRequest(init.FileId, init.ObjectKey, "silent.zip", "application/zip", 1024),
            null, "10.0.0.6", "ua");

        Assert.That(recorder.Published, Is.Empty);
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<NotificationPublishRequest> Published { get; } = [];

        public Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
        {
            Published.Add(request);
            return Task.FromResult(new AppNotification
            {
                Id = Guid.NewGuid(),
                EventType = request.EventType,
                Topic = request.Topic,
                Title = request.Title,
                Message = request.Message,
                Severity = request.Severity,
                RecipientUserId = request.RecipientUserId,
                CreatedBy = request.CreatedBy
            });
        }

        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
            => Task.FromResult<IReadOnlyList<AppNotification>>([]);

        public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
            => Task.FromResult(true);

        public Task<bool> DeleteAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null)
            => Task.FromResult(true);
    }

    private static ManagedFile MakeFile(string name, FileScope scope, Guid? clientId, Guid? siteId, FileVisibility vis) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        FileName = name + ".bin",
        Scope = scope,
        ClientId = clientId,
        SiteId = siteId,
        Visibility = vis,
        StorageObjectKey = $"file-hub/test/{Guid.NewGuid():N}/{name}.bin",
        StorageBucket = "test",
        ContentType = "application/octet-stream",
        SizeBytes = 1024,
        IsActive = true
    };

    private sealed class StubStorageFactory(IObjectStorageService storage) : IObjectStorageProviderFactory
    {
        public IObjectStorageService CreateObjectStorageService() => storage;
        public Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default) => Task.FromResult(storage);
        public Task<List<string>> ValidateConfigurationAsync() => Task.FromResult(new List<string>());
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ObjectStorageTestResult(true, true, true, [], 0));
    }

    private sealed class InMemoryStubStorage : IObjectStorageService
    {
        public long NextMetadataSizeBytes { get; set; } = 1024;
        public List<string> DeletedKeys { get; } = [];

        public Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DownloadToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default) { DeletedKeys.Add(objectKey); return Task.CompletedTask; }
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default) => Task.FromResult($"https://s3.local/{objectKey}");
        public Task<string> GetPresignedDownloadUrlAsync(string objectKey, TimeSpan ttl, string? downloadFileName, string? responseContentType = null, CancellationToken cancellationToken = default)
            => Task.FromResult($"https://s3.local/{objectKey}?disp={Uri.EscapeDataString(downloadFileName ?? string.Empty)}");
        public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default) => Task.FromResult($"https://s3.local/put/{objectKey}");
        public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, long? contentLengthBytes, CancellationToken cancellationToken = default) => Task.FromResult($"https://s3.local/put/{objectKey}");
        public Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult<StorageObject?>(new StorageObject { ObjectKey = objectKey, Bucket = "test", SizeBytes = NextMetadataSizeBytes, ContentType = "application/zip" });
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ObjectStorageTestResult(true, true, true, [], 0));
    }
}

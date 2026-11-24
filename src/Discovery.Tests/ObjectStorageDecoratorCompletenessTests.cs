using System.Reflection;
using System.Text;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Discovery.Infrastructure.Services.ObjectStorage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Garante que decorators e providers internos de <see cref="IObjectStorageService"/>
/// implementam todos os métodos da interface explicitamente (evita que default
/// interface methods engulam chamadas em silêncio, como aconteceu com
/// ListMultipartUploadSessionsAsync) e valida telemetria/segurança de anexos.
/// </summary>
[TestFixture]
public class ObjectStorageDecoratorCompletenessTests
{
    [Test]
    public void Decorators_ExplicitlyDeclareAllIObjectStorageServiceMethods()
    {
        var interfaceMethods = typeof(IObjectStorageService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)
            .Select(FormatSignature)
            .ToHashSet(StringComparer.Ordinal);

        var factoryAssembly = typeof(ObjectStorageProviderFactory).Assembly;
        var providerTypes = new List<Type>
        {
            typeof(InstrumentedObjectStorageProvider),
            typeof(MinioObjectStorageProvider),
            typeof(LocalObjectStorageProvider),
        };

        var nestedProviders = factoryAssembly.GetTypes()
            .Where(t => typeof(IObjectStorageService).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract
                        && (t.Name.Contains("NotConfiguredObjectStorageProvider", StringComparison.Ordinal)
                            || t.Name.Contains("DeferredObjectStorageProvider", StringComparison.Ordinal)));
        providerTypes.AddRange(nestedProviders);

        Assert.That(providerTypes, Has.Count.GreaterThanOrEqualTo(5));

        foreach (var type in providerTypes)
        {
            var declared = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .Select(FormatSignature)
                .ToHashSet(StringComparer.Ordinal);

            var missing = interfaceMethods.Except(declared).ToArray();
            Assert.That(missing, Is.Empty, $"{type.Name} não declarou explicitamente: {string.Join(", ", missing)}");
        }
    }

    [Test]
    public async Task InstrumentedProvider_ForwardsListMultipartSessions_AndTracksDownloadToBytesOut()
    {
        var payload = Encoding.UTF8.GetBytes("hello-s3-stream-telemetry");
        var inner = new StubStorage(payload);
        var telemetry = new ObjectStorageTelemetry();
        var instrumented = new InstrumentedObjectStorageProvider(inner, telemetry);

        var sessions = await instrumented.ListMultipartUploadSessionsAsync("app-store/custom/", 10);
        Assert.That(sessions, Has.Count.EqualTo(1));
        Assert.That(sessions[0].UploadId, Is.EqualTo("up-1"));

        using var dest = new MemoryStream();
        await instrumented.DownloadToAsync("clients/1/a.bin", dest);

        var snapshot = telemetry.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(dest.ToArray(), Is.EqualTo(payload));
            Assert.That(snapshot.Downloads, Is.EqualTo(1));
            Assert.That(snapshot.BytesOut, Is.EqualTo(payload.Length));
        });
    }

    [Test]
    public async Task AttachmentComplete_UsesSingleMetadataCall_AndDeletesOversizedUpload()
    {
        var storage = new MetadataCheckStorage(actualSizeBytes: 5_000_000);
        var repo = new StubAttachmentRepository();
        var service = new AttachmentService(repo, new StubFactory(storage), NullLogger<AttachmentService>.Instance);

        var attachmentId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var key = $"clients/{clientId:N}/ticket/{entityId:N}/attachments/{attachmentId:N}/doc.pdf";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.CompletePresignedUploadAsync(
                attachmentId,
                "ticket",
                entityId,
                clientId,
                "doc.pdf",
                "application/pdf",
                expectedSizeBytes: 1_024,
                objectKey: key));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("Tamanho real"));
            Assert.That(storage.ExistsCalls, Is.Zero, "não deve chamar ExistsAsync antes de GetMetadataAsync");
            Assert.That(storage.GetMetadataCalls, Is.EqualTo(1));
            Assert.That(storage.DeletedKeys, Is.EquivalentTo(new[] { key }));
        });
    }

    private static string FormatSignature(MethodInfo m)
        => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName))})";

    private sealed class StubStorage(byte[] payload) : IObjectStorageService
    {
        public Task<IReadOnlyList<MultipartUploadSessionInfo>> ListMultipartUploadSessionsAsync(
            string? prefix, int maxSessions, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MultipartUploadSessionInfo>>(
                [new MultipartUploadSessionInfo("app-store/custom/x.exe", "up-1", DateTime.UtcNow)]);

        public async Task DownloadToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default)
            => await destination.WriteAsync(payload, cancellationToken);

        public Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class MetadataCheckStorage(long actualSizeBytes) : IObjectStorageService
    {
        public int ExistsCalls { get; private set; }
        public int GetMetadataCalls { get; private set; }
        public List<string> DeletedKeys { get; } = [];

        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            ExistsCalls++;
            return Task.FromResult(true);
        }

        public Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            GetMetadataCalls++;
            return Task.FromResult<StorageObject?>(new StorageObject
            {
                ObjectKey = objectKey,
                Bucket = "test-bucket",
                SizeBytes = actualSizeBytes,
                ContentType = "application/pdf",
            });
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(objectKey);
            return Task.CompletedTask;
        }

        public Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DownloadToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class StubFactory(IObjectStorageService storage) : IObjectStorageProviderFactory
    {
        public IObjectStorageService CreateObjectStorageService() => storage;
        public Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default) => Task.FromResult(storage);
        public Task<List<string>> ValidateConfigurationAsync() => Task.FromResult(new List<string>());
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ObjectStorageTestResult(true, true, true, [], 0));
    }

    private sealed class StubAttachmentRepository : IAttachmentRepository
    {
        public Task<Attachment?> GetByIdAsync(Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult<Attachment?>(null);
        public Task<Attachment> CreateAsync(Attachment attachment, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Attachment> UpdateAsync(Attachment attachment, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Attachment> SoftDeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> HardDeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<int> SoftDeleteByEntityAsync(string entityType, Guid entityId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<List<Attachment>> GetByEntityAsync(string entityType, Guid entityId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<List<Attachment>> GetByClientIdAsync(Guid clientId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<List<Attachment>> GetSoftDeletedOlderThanAsync(int days, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> CountByEntityAsync(string entityType, Guid entityId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}

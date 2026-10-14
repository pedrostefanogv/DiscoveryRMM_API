using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Exclusão definitiva no SERVIÇO real: o objeto sai do storage ANTES da linha do
/// banco (se o storage falhar, o anexo continua rastreável). É o caminho
/// irreversível — precisa de teste próprio.
/// </summary>
public class KnowledgeImagePermanentDeleteServiceTests
{
    private static readonly Guid AttachmentId = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

    private static Attachment Image() => new()
    {
        Id = AttachmentId,
        EntityType = KnowledgeImageRules.EntityType,
        EntityId = Guid.NewGuid(),
        FileName = "print.png",
        ContentType = "image/png",
        StorageObjectKey = "global/knowledgeimage/art/attachments/" + AttachmentId.ToString("N") + "/print.png",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Test]
    public async Task PermanentDelete_RemovesObjectFromStorageThenRow()
    {
        var repository = new InMemoryAttachmentRepository(Image());
        var storage = new RecordingStorage();
        var service = new AttachmentService(repository, new StubStorageFactory(storage), NullLogger<AttachmentService>.Instance);

        var removed = await service.PermanentlyDeleteAttachmentAsync(AttachmentId);

        Assert.That(removed, Is.True);
        Assert.That(storage.DeletedKeys, Is.EqualTo(new[] { Image().StorageObjectKey }));
        Assert.That(repository.HardDeletedId, Is.EqualTo(AttachmentId));
    }

    [Test]
    public async Task PermanentDelete_WhenStorageFails_KeepsRowForRetry()
    {
        var repository = new InMemoryAttachmentRepository(Image());
        var storage = new RecordingStorage { ThrowOnDelete = true };
        var service = new AttachmentService(repository, new StubStorageFactory(storage), NullLogger<AttachmentService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.PermanentlyDeleteAttachmentAsync(AttachmentId));

        Assert.That(repository.HardDeletedId, Is.Null, "linha não pode ser apagada se o storage falhou");
    }

    [Test]
    public async Task PermanentDelete_UnknownAttachment_ReturnsFalseWithoutTouchingStorage()
    {
        var repository = new InMemoryAttachmentRepository(Image());
        var storage = new RecordingStorage();
        var service = new AttachmentService(repository, new StubStorageFactory(storage), NullLogger<AttachmentService>.Instance);

        var removed = await service.PermanentlyDeleteAttachmentAsync(Guid.NewGuid());

        Assert.That(removed, Is.False);
        Assert.That(storage.DeletedKeys, Is.Empty);
    }

    private sealed class StubStorageFactory(IObjectStorageService storage) : IObjectStorageProviderFactory
    {
        public IObjectStorageService CreateObjectStorageService() => storage;
        public Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default) => Task.FromResult(storage);
        public Task<List<string>> ValidateConfigurationAsync() => Task.FromResult(new List<string>());
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ObjectStorageTestResult(true, true, true, [], 0));
    }

    private sealed class RecordingStorage : IObjectStorageService
    {
        public List<string> DeletedKeys { get; } = [];
        public bool ThrowOnDelete { get; init; }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDelete) throw new InvalidOperationException("storage fora do ar");
            DeletedKeys.Add(objectKey);
            return Task.CompletedTask;
        }

        public Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DownloadToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class InMemoryAttachmentRepository(Attachment attachment) : IAttachmentRepository
    {
        public Guid? HardDeletedId { get; private set; }

        public Task<Attachment?> GetByIdAsync(Guid attachmentId, CancellationToken cancellationToken = default)
            => Task.FromResult<Attachment?>(HardDeletedId is null && attachmentId == attachment.Id ? attachment : null);

        public Task<bool> HardDeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default)
        {
            HardDeletedId = attachmentId;
            return Task.FromResult(true);
        }

        public Task<Attachment> CreateAsync(Attachment a, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Attachment> UpdateAsync(Attachment a, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Attachment> SoftDeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> SoftDeleteByEntityAsync(string entityType, Guid entityId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<List<Attachment>> GetByEntityAsync(string entityType, Guid entityId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<List<Attachment>> GetByClientIdAsync(Guid clientId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<List<Attachment>> GetSoftDeletedOlderThanAsync(int days, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> CountByEntityAsync(string entityType, Guid entityId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}

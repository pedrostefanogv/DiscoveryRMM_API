using System.Text;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes do upload de assets de pacotes custom da Loja de Softwares:
/// composição da object key, validação de extensão e propagação de metadados.
/// </summary>
public class CustomPackageAssetServiceTests
{
    [Test]
    public async Task Upload_Installer_CompoeObjectKeyEPreservaMetadados()
    {
        var storage = new CapturingStorageService();
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("MZ"));
        var asset = await service.UploadAsync("installer", "Setup XPTO v1.2.exe", "application/x-msdownload", content);

        Assert.That(asset.ObjectKey, Does.StartWith("app-store/custom/installers/"));
        Assert.That(asset.ObjectKey, Does.EndWith("Setup XPTO v1.2.exe"));
        Assert.That(asset.Bucket, Is.EqualTo("discovery-test"));
        Assert.That(asset.ContentType, Is.EqualTo("application/x-msdownload"));
        Assert.That(asset.SizeBytes, Is.EqualTo(2));
        Assert.That(storage.LastKey, Is.EqualTo(asset.ObjectKey));
    }

    [Test]
    public async Task Upload_IconPng_Permitido()
    {
        var service = new CustomPackageAssetService(new StubStorageFactory(new CapturingStorageService()));

        await using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var asset = await service.UploadAsync("icon", "capa.png", "image/png", content);

        Assert.That(asset.ObjectKey, Does.StartWith("app-store/custom/icons/"));
    }

    [Test]
    public async Task Upload_InstallerComExtensaoNaoPermitida_Falha()
    {
        var service = new CustomPackageAssetService(new StubStorageFactory(new CapturingStorageService()));

        using var content = new MemoryStream(new byte[] { 1 });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.UploadAsync("installer", "notas.txt", "text/plain", content);
        });

        Assert.That(ex!.Message, Does.Contain("não permitida"));
    }

    [Test]
    public async Task Upload_IconeComTipoInvalido_Falha()
    {
        var service = new CustomPackageAssetService(new StubStorageFactory(new CapturingStorageService()));

        using var content = new MemoryStream(new byte[] { 1 });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.UploadAsync("icon", "arquivo.exe", "application/x-msdownload", content);
        });

        Assert.That(ex!.Message, Does.Contain("imagem"));
    }

    [Test]
    public async Task Upload_KindInvalido_Falha()
    {
        var service = new CustomPackageAssetService(new StubStorageFactory(new CapturingStorageService()));

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.UploadAsync("outro", "setup.exe", "application/octet-stream", content);
        });
    }

    private sealed class StubStorageFactory(IObjectStorageService storage) : IObjectStorageProviderFactory
    {
        public IObjectStorageService CreateObjectStorageService() => storage;
        public Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default) => Task.FromResult(storage);
        public Task<List<string>> ValidateConfigurationAsync() => Task.FromResult(new List<string>());
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ObjectStorageTestResult(true, true, true, [], 1));
    }

    private sealed class CapturingStorageService : IObjectStorageService
    {
        public string? LastKey { get; private set; }

        public async Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            LastKey = objectKey;
            var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            return new StorageObject
            {
                ObjectKey = objectKey,
                Bucket = "discovery-test",
                ContentType = contentType,
                SizeBytes = buffer.Length,
                Checksum = "abc123",
                StoredAt = DateTime.UtcNow
            };
        }

        public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default) => Task.FromResult("https://example.invalid/object");
        public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default) => Task.FromResult("https://example.invalid/upload");
        public Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<StorageObject?>(null);
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ObjectStorageTestResult(true, true, true, [], 1));
    }
}

using System.Security.Cryptography;
using System.Text;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes do upload MULTIPART DIRETO no storage (instaladores grandes atrás do
/// Cloudflare): composição do multipart, validação, pré-assinatura das partes,
/// finalização com ETags e o SHA-256 calculado depois do complete.
/// </summary>
public class CustomPackageAssetMultipartTests
{
    private const string ObjectKeySample = "app-store/custom/installers/abc123/setup.exe";

    [Test]
    public async Task Begin_CriaMultipartNoStorageComBlocoDe10MiB()
    {
        var storage = new MultipartStorage();
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        var session = await service.BeginMultipartUploadAsync(
            "installer", "Setup XPTO v2.exe", "application/x-msdownload", 1_900_000_000);

        Assert.That(storage.BeginKey, Does.StartWith("app-store/custom/installers/"));
        Assert.That(storage.BeginKey, Does.EndWith("Setup XPTO v2.exe"));
        Assert.That(session.UploadId, Is.EqualTo("upload-abc"));
        Assert.That(session.ObjectKey, Is.EqualTo(storage.BeginKey));
        Assert.That(session.PartSizeBytes, Is.EqualTo(10L * 1024 * 1024));
        Assert.That(session.PartUrlTtlMinutes, Is.EqualTo(60));
        Assert.That(session.SizeBytes, Is.EqualTo(1_900_000_000));
        Assert.That(session.Kind, Is.EqualTo("installer"));
    }

    [Test]
    public async Task Begin_ExtensaoNaoPermitida_Falha()
    {
        var service = new CustomPackageAssetService(new StubStorageFactory(new MultipartStorage()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.BeginMultipartUploadAsync("installer", "notas.txt", "text/plain", 1024));

        Assert.That(ex!.Message, Does.Contain("não permitida"));
    }

    [Test]
    public async Task Begin_TamanhoZero_Falha()
    {
        var service = new CustomPackageAssetService(new StubStorageFactory(new MultipartStorage()));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.BeginMultipartUploadAsync("installer", "setup.exe", "application/octet-stream", 0));
    }

    [Test]
    public async Task GetUploadPartUrl_AssinaAPartePedida()
    {
        var storage = new MultipartStorage();
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        var part = await service.GetUploadPartUrlAsync(ObjectKeySample, "upload-abc", 7);

        Assert.That(part.PartNumber, Is.EqualTo(7));
        Assert.That(storage.LastPartNumber, Is.EqualTo(7));
        Assert.That(storage.LastPartTtlMinutes, Is.EqualTo(60));
        Assert.That(part.Url, Does.Contain("partNumber=7"));
        Assert.That(part.Url, Does.Contain("uploadId=upload-abc"));
        Assert.That(part.ExpiresInSeconds, Is.EqualTo(3600));
    }

    [Test]
    public async Task GetUploadPartUrl_ParteForaDoIntervalo_Falha()
    {
        var service = new CustomPackageAssetService(new StubStorageFactory(new MultipartStorage()));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.GetUploadPartUrlAsync(ObjectKeySample, "upload-abc", 0));
    }

    [Test]
    public async Task Complete_SemPartes_Falha()
    {
        var service = new CustomPackageAssetService(new StubStorageFactory(new MultipartStorage()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.CompleteMultipartUploadAsync(
                ObjectKeySample, "installer", "setup.exe", "application/octet-stream", 1024, "upload-abc", []));

        Assert.That(ex!.Message, Does.Contain("Nenhuma parte"));
    }

    [Test]
    public async Task Complete_EnviaEtagsEmOrdemECalculaSha256()
    {
        var payload = Encoding.UTF8.GetBytes("conteudo-montado-pelo-storage");
        var storage = new MultipartStorage { Content = payload };
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        // Tamanhos coerentes com o bloco padrão de 50 MiB: 1 bloco cheio + resto.
        const long partSize = CustomPackageAssetService.PartSizeBytes;
        var declaredSize = partSize + payload.Length;

        var asset = await service.CompleteMultipartUploadAsync(
            ObjectKeySample,
            "installer",
            "Setup.exe",
            "application/x-msdownload",
            declaredSize,
            "upload-abc",
            [
                new Discovery.Core.DTOs.MultipartUploadPartStateDto(2, "etag-2", payload.Length),
                new Discovery.Core.DTOs.MultipartUploadPartStateDto(1, "etag-1", partSize),
            ]);

        // A ordem enviada ao storage é o que o S3 aceita (partes crescentes).
        Assert.That(storage.CompletedParts!.Select(part => part.PartNumber), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(storage.CompletedParts!.Select(part => part.ETag), Is.EqualTo(new[] { "etag-1", "etag-2" }));
        Assert.That(storage.CompletedUploadId, Is.EqualTo("upload-abc"));

        // O arquivo nunca passou pela API: o SHA-256 vem da passada de leitura.
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        Assert.That(asset.Checksum, Is.EqualTo(expected));
        Assert.That(storage.DownloadToCalled, Is.True);
        Assert.That(asset.SizeBytes, Is.EqualTo(payload.Length));
        Assert.That(asset.Bucket, Is.EqualTo("discovery-files"));
        Assert.That(asset.FileName, Is.EqualTo("Setup.exe"));
    }

    [Test]
    public async Task Complete_QuandoObjetoJaExiste_NaoFalha()
    {
        // Cenário real: um upload de vários GiB concluiu no storage, mas a
        // resposta se perdeu (timeout do Cloudflare/nginx). A retentativa veria
        // "upload inexistente"; se o objeto já está lá, o asset é devolvido.
        var payload = Encoding.UTF8.GetBytes("instalador-grande");
        var storage = new MultipartStorage { Content = payload, FailComplete = true };
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        var asset = await service.CompleteMultipartUploadAsync(
            ObjectKeySample, "installer", "Setup.exe", "application/x-msdownload", payload.Length, "upload-abc",
            [new Discovery.Core.DTOs.MultipartUploadPartStateDto(1, "etag-1", payload.Length)]);

        Assert.That(asset.ObjectKey, Is.EqualTo(ObjectKeySample));
        Assert.That(asset.SizeBytes, Is.EqualTo(payload.Length));
        Assert.That(asset.Checksum, Is.Not.Empty);
    }

    [Test]
    public async Task Complete_AceitaSessaoAntigaComOutroTamanhoDeBloco()
    {
        // Regressão: o padrão do bloco mudou de 50 MiB para 10 MiB, mas sessões
        // abertas antes (TTL 24h) precisam continuar podendo ser concluídas — a
        // validação deriva o tamanho das próprias partes, não da constante.
        const long oldPartSize = 50L * 1024 * 1024;
        var storage = new MultipartStorage { Content = Encoding.UTF8.GetBytes("x") };
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        var asset = await service.CompleteMultipartUploadAsync(
            ObjectKeySample, "installer", "Setup.exe", "application/x-msdownload",
            oldPartSize + 5, "upload-velho",
            [
                new Discovery.Core.DTOs.MultipartUploadPartStateDto(1, "etag-1", oldPartSize),
                new Discovery.Core.DTOs.MultipartUploadPartStateDto(2, "etag-2", 5),
            ]);

        Assert.That(asset.ObjectKey, Is.EqualTo(ObjectKeySample));
        Assert.That(storage.CompletedParts!.Select(part => part.PartNumber), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task Complete_ComBlocosFaltando_FalhaAntesDeIrAoStorage()
    {
        var payload = Encoding.UTF8.GetBytes("x");
        var storage = new MultipartStorage { Content = payload };
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        // Faltou o bloco 1 (só veio o 2): o S3 recusaria o complete, então a API
        // precisa falhar com uma mensagem que diga o que reenviar.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.CompleteMultipartUploadAsync(
                ObjectKeySample, "installer", "Setup.exe", "application/x-msdownload",
                CustomPackageAssetService.PartSizeBytes + payload.Length, "upload-abc",
                [new Discovery.Core.DTOs.MultipartUploadPartStateDto(2, "etag-2", payload.Length)]));

        Assert.That(ex!.Message, Does.Contain("esperava a parte 1"));
        Assert.That(storage.CompletedParts, Is.Null, "não deve chamar o storage com lista inválida");
    }

    [Test]
    public async Task Complete_ComChecksumConhecido_NaoRelaOArquivo()
    {
        // Retentativa de um complete cuja resposta se perdeu: o SHA-256 já foi
        // calculado e reler um instalador de vários GiB é o passo mais caro do
        // fluxo (e o que estoura o timeout do Cloudflare).
        var storage = new MultipartStorage { Content = Encoding.UTF8.GetBytes("x") };
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));
        const long partSize = CustomPackageAssetService.PartSizeBytes;

        var asset = await service.CompleteMultipartUploadAsync(
            ObjectKeySample, "installer", "Setup.exe", "application/x-msdownload",
            partSize + 1, "upload-abc",
            [
                new Discovery.Core.DTOs.MultipartUploadPartStateDto(1, "etag-1", partSize),
                new Discovery.Core.DTOs.MultipartUploadPartStateDto(2, "etag-2", 1),
            ],
            knownChecksum: "abc123");

        Assert.That(asset.Checksum, Is.EqualTo("abc123"));
        Assert.That(storage.DownloadToCalled, Is.False, "não deve reler o arquivo com checksum conhecido");
    }

    [Test]
    public async Task DescribeCompletedAsset_DevolveAssetSemRelerOArquivo()
    {
        // Retentativa de complete quando o multipart já não existe no storage: a
        // resposta correta é o asset publicado, sem reenvio nem releitura.
        var storage = new MultipartStorage { Content = [1, 2, 3] };
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        var described = await service.DescribeCompletedAssetAsync(ObjectKeySample, "Setup.exe", "sha-conhecido");

        Assert.That(described, Is.Not.Null);
        Assert.That(described!.Checksum, Is.EqualTo("sha-conhecido"));
        Assert.That(described.SizeBytes, Is.EqualTo(3));
        Assert.That(described.Bucket, Is.EqualTo("discovery-files"));
        Assert.That(storage.DownloadToCalled, Is.False, "não deve reler o arquivo para descrever o asset");
    }

    [Test]
    public async Task DescribeCompletedAsset_ObjetoInexistente_DevolveNull()
    {
        var storage = new MultipartStorage { MetadataMissing = true };
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        Assert.That(await service.DescribeCompletedAssetAsync(ObjectKeySample, "Setup.exe"), Is.Null);
    }

    [Test]
    public async Task ListUploadedParts_OrdenaEPreservaEtag()
    {
        var storage = new MultipartStorage
        {
            ReceivedParts =
            [
                new MultipartUploadPart(3, "etag-3", 30),
                new MultipartUploadPart(1, "etag-1", 10),
            ],
        };
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        var parts = await service.ListUploadedPartsAsync(ObjectKeySample, "upload-abc");

        Assert.That(parts.Select(part => part.PartNumber), Is.EqualTo(new[] { 1, 3 }));
        Assert.That(parts.Select(part => part.ETag), Is.EqualTo(new[] { "etag-1", "etag-3" }));
    }

    [Test]
    public async Task Abort_DelegaParaOStorage()
    {
        var storage = new MultipartStorage();
        var service = new CustomPackageAssetService(new StubStorageFactory(storage));

        await service.AbortMultipartUploadAsync(ObjectKeySample, "upload-abc");

        Assert.That(storage.AbortedUploadId, Is.EqualTo("upload-abc"));
        Assert.That(storage.AbortedKey, Is.EqualTo(ObjectKeySample));
    }

    private sealed class StubStorageFactory(IObjectStorageService storage) : IObjectStorageProviderFactory
    {
        public IObjectStorageService CreateObjectStorageService() => storage;
        public Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default) => Task.FromResult(storage);
        public Task<List<string>> ValidateConfigurationAsync() => Task.FromResult(new List<string>());
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ObjectStorageTestResult(true, true, true, [], 1));
    }

    private sealed class MultipartStorage : IObjectStorageService
    {
        public byte[] Content { get; init; } = [];
        public IReadOnlyList<MultipartUploadPart> ReceivedParts { get; init; } = [];
        public bool FailComplete { get; init; }
        /// <summary>Simula objeto ausente no storage.</summary>
        public bool MetadataMissing { get; init; }

        public string? BeginKey { get; private set; }
        public int? LastPartNumber { get; private set; }
        public int? LastPartTtlMinutes { get; private set; }
        public IReadOnlyList<MultipartUploadPart>? CompletedParts { get; private set; }
        public string? CompletedUploadId { get; private set; }
        public string? AbortedUploadId { get; private set; }
        public string? AbortedKey { get; private set; }
        public bool DownloadToCalled { get; private set; }

        public Task<string> CreateMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken = default)
        {
            BeginKey = objectKey;
            return Task.FromResult("upload-abc");
        }

        public Task<string> GetPresignedUploadPartUrlAsync(string objectKey, string uploadId, int partNumber, int ttlMinutes, CancellationToken cancellationToken = default)
        {
            LastPartNumber = partNumber;
            LastPartTtlMinutes = ttlMinutes;
            return Task.FromResult($"https://storage.test/discovery-files/{objectKey}?partNumber={partNumber}&uploadId={uploadId}&X-Amz-Signature=abc");
        }

        public Task CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<MultipartUploadPart> parts, CancellationToken cancellationToken = default)
        {
            CompletedUploadId = uploadId;
            CompletedParts = parts;

            return FailComplete
                ? Task.FromException(new InvalidOperationException("NoSuchUpload: o upload multipart nao existe mais."))
                : Task.CompletedTask;
        }

        public Task AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken cancellationToken = default)
        {
            AbortedKey = objectKey;
            AbortedUploadId = uploadId;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MultipartUploadPart>> ListMultipartUploadPartsAsync(string objectKey, string uploadId, CancellationToken cancellationToken = default)
            => Task.FromResult(ReceivedParts);

        public async Task DownloadToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default)
        {
            DownloadToCalled = true;
            await destination.WriteAsync(Content, cancellationToken);
        }

        public Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<StorageObject?>(MetadataMissing ? null : new StorageObject
            {
                ObjectKey = objectKey,
                Bucket = "discovery-files",
                ContentType = "application/x-msdownload",
                SizeBytes = Content.Length,
                Checksum = "etag-multipart",
                StorageProvider = ObjectStorageProviderType.S3Compatible,
                StoredAt = DateTime.UtcNow,
            });

        public Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default) => Task.FromResult("https://storage.test/object");
        public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default) => Task.FromResult("https://storage.test/upload");
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ObjectStorageTestResult(true, true, true, [], 1));
    }
}

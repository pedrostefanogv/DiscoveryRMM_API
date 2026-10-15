using System.Text;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação de ICustomPackageAssetService sobre o object storage
/// configurado (S3/RustFS/MinIO). Os objetos ficam sob
/// `app-store/custom/{installers|icons}/{guid}/{fileName}`, isolando cada envio.
/// </summary>
public class CustomPackageAssetService : ICustomPackageAssetService
{
    private const string InstallerKind = "installer";
    private const string IconKind = "icon";

    // O protocolo S3 recusa PUT único acima de 5 GiB; o upload multipart direto
    // remove esse teto (limite real = 10000 partes x 50 MiB).
    public const long MaxAssetBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>Bloco padrão do upload multipart direto: 50 MiB (padronizado).</summary>
    public const long PartSizeBytes = 50L * 1024 * 1024;

    /// <summary>Validade da URL pré-assinada de cada parte (upload longo cabe).</summary>
    public const int PartUrlTtlMinutes = 60;

    private static readonly HashSet<string> AllowedInstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".zip", ".7z", ".rar",
        ".ps1", ".bat", ".cmd", ".msu", ".cab", ".iso", ".tar", ".gz", ".pkg", ".dmg"
    };

    private static readonly HashSet<string> AllowedIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".ico", ".bmp"
    };

    private readonly IObjectStorageProviderFactory _storageFactory;
    private readonly ILogger<CustomPackageAssetService>? _logger;

    public CustomPackageAssetService(
        IObjectStorageProviderFactory storageFactory,
        ILogger<CustomPackageAssetService>? logger = null)
    {
        _storageFactory = storageFactory;
        _logger = logger;
    }

    public async Task<CustomPackageAssetDto> UploadAsync(
        string kind,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var normalizedKind = NormalizeKind(kind);
        var safeName = SanitizeFileName(fileName);
        ValidateAsset(normalizedKind, safeName, contentType, content);

        var objectKey = $"app-store/custom/{normalizedKind}s/{Guid.NewGuid():N}/{safeName}";
        var storage = await _storageFactory.CreateObjectStorageServiceAsync(cancellationToken);

        // Calcula o SHA-256 durante o upload (sem segunda passada) para que o
        // agent possa verificar a integridade do instalador depois de baixar.
        using var hashing = new HashingReadStream(content, System.Security.Cryptography.HashAlgorithmName.SHA256);
        var stored = await storage.UploadAsync(
            objectKey,
            hashing,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            cancellationToken);

        return new CustomPackageAssetDto
        {
            ObjectKey = stored.ObjectKey,
            Bucket = stored.Bucket,
            ContentType = string.IsNullOrWhiteSpace(stored.ContentType) ? contentType : stored.ContentType,
            FileName = safeName,
            SizeBytes = stored.SizeBytes,
            Checksum = hashing.GetHashHex()
        };
    }

    // ── Upload multipart DIRETO no storage ───────────────────────────────────

    public async Task<MultipartUploadSessionDto> BeginMultipartUploadAsync(
        string kind,
        string fileName,
        string contentType,
        long sizeBytes,
        CancellationToken cancellationToken = default)
    {
        var normalizedKind = NormalizeKind(kind);
        var safeName = SanitizeFileName(fileName);
        ValidateAssetMetadata(normalizedKind, safeName, contentType, sizeBytes);

        var maxMultipartBytes = PartSizeBytes * 10_000;
        if (sizeBytes > maxMultipartBytes)
            throw new InvalidOperationException(
                $"Arquivo excede o limite de {maxMultipartBytes / (1024L * 1024 * 1024)} GiB do upload multipart.");

        var objectKey = $"app-store/custom/{normalizedKind}s/{Guid.NewGuid():N}/{safeName}";
        var storage = await _storageFactory.CreateObjectStorageServiceAsync(cancellationToken);

        var resolvedContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        var uploadId = await storage.CreateMultipartUploadAsync(objectKey, resolvedContentType, cancellationToken);

        return new MultipartUploadSessionDto(
            UploadId: uploadId,
            ObjectKey: objectKey,
            // O bucket nao e exposto pela interface do storage; o valor real do
            // asset vem do GetMetadataAsync no complete (que e o DTO final).
            Bucket: string.Empty,
            Kind: normalizedKind,
            FileName: safeName,
            ContentType: resolvedContentType,
            SizeBytes: sizeBytes,
            PartSizeBytes: PartSizeBytes,
            PartUrlTtlMinutes: PartUrlTtlMinutes);
    }

    public async Task<MultipartUploadPartUrlDto> GetUploadPartUrlAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        CancellationToken cancellationToken = default)
    {
        if (partNumber is < 1 or > 10_000)
            throw new InvalidOperationException($"Número de parte inválido: {partNumber}.");

        var storage = await _storageFactory.CreateObjectStorageServiceAsync(cancellationToken);
        var url = await storage.GetPresignedUploadPartUrlAsync(
            objectKey, uploadId, partNumber, PartUrlTtlMinutes, cancellationToken);

        return new MultipartUploadPartUrlDto(partNumber, url, PartUrlTtlMinutes * 60);
    }

    public async Task<IReadOnlyList<MultipartUploadPartStateDto>> ListUploadedPartsAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        var storage = await _storageFactory.CreateObjectStorageServiceAsync(cancellationToken);
        var parts = await storage.ListMultipartUploadPartsAsync(objectKey, uploadId, cancellationToken);

        return parts
            .Select(part => new MultipartUploadPartStateDto(part.PartNumber, part.ETag, part.SizeBytes))
            .OrderBy(part => part.PartNumber)
            .ToArray();
    }

    public async Task<CustomPackageAssetDto> CompleteMultipartUploadAsync(
        string objectKey,
        string kind,
        string fileName,
        string contentType,
        long sizeBytes,
        string uploadId,
        IReadOnlyList<MultipartUploadPartStateDto> parts,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new InvalidOperationException("ObjectKey é obrigatório.");
        if (parts is null || parts.Count == 0)
            throw new InvalidOperationException("Nenhuma parte recebida: não há o que finalizar.");

        var normalizedKind = NormalizeKind(kind);
        var safeName = SanitizeFileName(fileName);
        var storage = await _storageFactory.CreateObjectStorageServiceAsync(cancellationToken);

        // O S3 exige as partes em ordem crescente de número no complete.
        var orderedParts = parts
            .OrderBy(part => part.PartNumber)
            .Select(part => new MultipartUploadPart(part.PartNumber, part.ETag, part.SizeBytes))
            .ToArray();

        try
        {
            await storage.CompleteMultipartUploadAsync(objectKey, uploadId, orderedParts, cancellationToken);
        }
        catch (Exception ex)
        {
            // Idempotência: um upload de vários GiB pode ter sido concluído no
            // storage e a resposta perdida no caminho (Cloudflare/nginx timeout).
            // A segunda tentativa veria "upload inexistente" — mas o objeto está
            // lá, então o correto é devolver o asset, não falhar.
            if (!await storage.ExistsAsync(objectKey, cancellationToken))
                throw;

            _logger?.LogWarning(
                ex,
                "Complete multipart reapresentado para {ObjectKey}: objeto já existe no storage; tratando como concluído.",
                objectKey);
        }

        var metadata = await storage.GetMetadataAsync(objectKey, cancellationToken);
        var storedSize = metadata?.SizeBytes ?? sizeBytes;

        // O arquivo foi montado pelo storage (nunca passou pela API): o SHA-256 é
        // calculado numa passada de leitura pelo endpoint interno (loopback).
        var checksum = await TryComputeChecksumAsync(storage, objectKey, cancellationToken);

        return new CustomPackageAssetDto
        {
            ObjectKey = objectKey,
            Bucket = metadata?.Bucket ?? string.Empty,
            ContentType = string.IsNullOrWhiteSpace(metadata?.ContentType) ? contentType : metadata!.ContentType,
            FileName = safeName,
            SizeBytes = storedSize,
            Checksum = checksum
        };
    }

    public async Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || string.IsNullOrWhiteSpace(uploadId))
            return;

        var storage = await _storageFactory.CreateObjectStorageServiceAsync(cancellationToken);
        await storage.AbortMultipartUploadAsync(objectKey, uploadId, cancellationToken);
    }

    private async Task<string> TryComputeChecksumAsync(
        IObjectStorageService storage,
        string objectKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var hashing = new HashingWriteStream(System.Security.Cryptography.HashAlgorithmName.SHA256);
            await storage.DownloadToAsync(objectKey, hashing, cancellationToken);
            return hashing.GetHashHex();
        }
        catch (Exception)
        {
            // Não invalida um upload que já foi concluído com sucesso no storage:
            // apenas fica sem checksum (o agent cai no fallback de tamanho/ETag).
            return string.Empty;
        }
    }

    public async Task<CustomPackageAssetContent> DownloadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("ObjectKey é obrigatório.", nameof(objectKey));

        var storage = await _storageFactory.CreateObjectStorageServiceAsync(cancellationToken);
        var stream = await storage.DownloadAsync(objectKey, cancellationToken);
        var metadata = await storage.GetMetadataAsync(objectKey, cancellationToken);

        return new CustomPackageAssetContent
        {
            Content = stream,
            ContentType = string.IsNullOrWhiteSpace(metadata?.ContentType)
                ? "application/octet-stream"
                : metadata!.ContentType,
            FileName = metadata?.GetFileName() ?? Path.GetFileName(objectKey)
        };
    }

    public async Task WriteToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("ObjectKey é obrigatório.", nameof(objectKey));

        ArgumentNullException.ThrowIfNull(destination);

        var storage = await _storageFactory.CreateObjectStorageServiceAsync(cancellationToken);
        await storage.DownloadToAsync(objectKey, destination, cancellationToken);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            return;

        var storage = await _storageFactory.CreateObjectStorageServiceAsync(cancellationToken);
        await storage.DeleteAsync(objectKey, cancellationToken);
    }

    private static string NormalizeKind(string kind)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or InstallerKind => InstallerKind,
            IconKind => IconKind,
            _ => throw new InvalidOperationException("kind inválido: use 'installer' ou 'icon'.")
        };
    }

    /// <summary>
    /// Mesmas regras do upload único, mas a partir dos metadados — no multipart
    /// direto a API nunca recebe o arquivo, só o tamanho declarado.
    /// </summary>
    private static void ValidateAssetMetadata(string kind, string fileName, string contentType, long sizeBytes)
    {
        if (sizeBytes <= 0)
            throw new InvalidOperationException("Arquivo inválido ou vazio.");

        var extension = Path.GetExtension(fileName);
        if (kind == IconKind)
        {
            if (!AllowedIconExtensions.Contains(extension) && !(contentType ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ícone deve ser uma imagem (png, jpg, webp, gif, svg ou ico).");
            return;
        }

        if (!AllowedInstallerExtensions.Contains(extension))
            throw new InvalidOperationException(
                $"Extensão '{extension}' não permitida para instalador. Extensões aceitas: {string.Join(", ", AllowedInstallerExtensions)}.");
    }

    private static void ValidateAsset(string kind, string fileName, string contentType, Stream content)
    {
        if (content is null || !content.CanRead)
            throw new InvalidOperationException("Arquivo inválido ou vazio.");

        // Guarda-corpo de tamanho: sem isso o limite existia só no atributo
        // [RequestSizeLimit] da controller e sumia em chamadas internas.
        if (content.CanSeek && content.Length > MaxAssetBytes)
            throw new InvalidOperationException(
                $"Arquivo excede o limite de {MaxAssetBytes / (1024L * 1024 * 1024)} GiB por asset.");

        var extension = Path.GetExtension(fileName);
        if (kind == IconKind)
        {
            if (!AllowedIconExtensions.Contains(extension) && !(contentType ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ícone deve ser uma imagem (png, jpg, webp, gif, svg ou ico).");
            return;
        }

        if (!AllowedInstallerExtensions.Contains(extension))
            throw new InvalidOperationException(
                $"Extensão '{extension}' não permitida para instalador. Extensões aceitas: {string.Join(", ", AllowedInstallerExtensions)}.");
    }

    /// <summary>Remove diretórios e caracteres perigosos do nome do arquivo.</summary>
    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "arquivo";

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or ' ' ? ch : '_');
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "arquivo" : sanitized[..Math.Min(sanitized.Length, 200)];
    }
}

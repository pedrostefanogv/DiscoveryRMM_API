using System.Text;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;

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

    // O protocolo S3 recusa PUT único acima de 5 GiB. Enquanto não houver
    // multipart, esse é o teto real do upload.
    public const long MaxAssetBytes = 5L * 1024 * 1024 * 1024;

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

    public CustomPackageAssetService(IObjectStorageProviderFactory storageFactory)
    {
        _storageFactory = storageFactory;
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
        var stored = await storage.UploadAsync(
            objectKey,
            content,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            cancellationToken);

        return new CustomPackageAssetDto
        {
            ObjectKey = stored.ObjectKey,
            Bucket = stored.Bucket,
            ContentType = string.IsNullOrWhiteSpace(stored.ContentType) ? contentType : stored.ContentType,
            FileName = safeName,
            SizeBytes = stored.SizeBytes,
            Checksum = stored.Checksum
        };
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

    private static void ValidateAsset(string kind, string fileName, string contentType, Stream content)
    {
        if (content is null || !content.CanRead)
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

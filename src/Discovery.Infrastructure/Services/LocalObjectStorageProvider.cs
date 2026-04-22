using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação local de Object Storage (apenas para desenvolvimento).
/// Armazena arquivos em disco local, simulando bucket structure.
/// 
/// Nunca usar em produção - apenas para testes e desenvolvimento.
/// A runtime factory (<see cref="ObjectStorageProviderFactory"/>) bloqueia o uso desta classe
/// em tempo de execução e retorna um <c>NotConfiguredObjectStorageProvider</c> quando solicitada.
/// Em produção (ASPNETCORE_ENVIRONMENT=Production) o construtor lança explicitamente.
/// </summary>
public class LocalObjectStorageProvider : IObjectStorageService
{
    private readonly string _basePath;
    private readonly ILogger<LocalObjectStorageProvider> _logger;

    public LocalObjectStorageProvider(ILogger<LocalObjectStorageProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? string.Empty;
        if (env.Equals("Production", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "LocalObjectStorageProvider cannot be used in a Production environment. " +
                "Configure a real S3-compatible provider in ServerConfiguration.");

        // Usar pasta app_data/object-storage no diretório da aplicação
        _basePath = Path.Combine(AppContext.BaseDirectory, "app_data", "object-storage");
        Directory.CreateDirectory(_basePath);

        _logger.LogWarning("LocalObjectStorageProvider initialized at {BasePath} - FOR DEVELOPMENT ONLY!", _basePath);
    }

    /// <summary>
    /// Fazer upload (copiar arquivo para disco local).
    /// </summary>
    public async Task<StorageObject> UploadAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key cannot be empty", nameof(objectKey));

        if (content == null)
            throw new ArgumentNullException(nameof(content));

        try
        {
            var filePath = NormalizeObjectKey(objectKey);
            var directoryPath = Path.GetDirectoryName(filePath);

            Directory.CreateDirectory(directoryPath!);

            using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                await content.CopyToAsync(fileStream, cancellationToken);
            }

            var fileInfo = new FileInfo(filePath);
            var checksum = ComputeChecksum(filePath);

            _logger.LogInformation("Uploaded object {ObjectKey} ({SizeBytes} bytes) to {FilePath}",
                objectKey, fileInfo.Length, filePath);

            return new StorageObject
            {
                ObjectKey = objectKey,
                Bucket = "local",
                ContentType = contentType,
                SizeBytes = fileInfo.Length,
                Checksum = checksum,
                StorageProvider = Core.Enums.ObjectStorageProviderType.Local,
                StoredAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading object {ObjectKey}", objectKey);
            throw;
        }
    }

    /// <summary>
    /// Fazer download (ler arquivo do disco local).
    /// </summary>
    public async Task<Stream> DownloadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key cannot be empty", nameof(objectKey));

        try
        {
            var filePath = NormalizeObjectKey(objectKey);

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Object {objectKey} not found");

            _logger.LogInformation("Downloading object {ObjectKey} from {FilePath}", objectKey, filePath);

            // Retornar MemoryStream com conteúdo do arquivo
            var memoryStream = new MemoryStream();
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                await fileStream.CopyToAsync(memoryStream, cancellationToken);
            }

            memoryStream.Seek(0, SeekOrigin.Begin);
            return memoryStream;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading object {ObjectKey}", objectKey);
            throw;
        }
    }

    /// <summary>
    /// Verificar se arquivo existe.
    /// </summary>
    public Task<bool> ExistsAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key cannot be empty", nameof(objectKey));

        var filePath = NormalizeObjectKey(objectKey);
        return Task.FromResult(File.Exists(filePath));
    }

    /// <summary>
    /// Deletar arquivo.
    /// </summary>
    public Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key cannot be empty", nameof(objectKey));

        try
        {
            var filePath = NormalizeObjectKey(objectKey);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Deleted object {ObjectKey}", objectKey);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting object {ObjectKey}", objectKey);
            throw;
        }
    }

    /// <summary>
    /// Deletar arquivos com prefixo.
    /// </summary>
    public Task DeleteByPrefixAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix cannot be empty", nameof(prefix));

        try
        {
            var prefixPath = NormalizeObjectKey(prefix);
            var baseDir = Path.GetDirectoryName(prefixPath) ?? _basePath;

            if (!Directory.Exists(baseDir))
                return Task.CompletedTask;

            var files = Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories)
                .Where(f => f.StartsWith(prefixPath))
                .ToList();

            foreach (var file in files)
            {
                File.Delete(file);
            }

            _logger.LogInformation("Deleted {Count} objects with prefix {Prefix}", files.Count, prefix);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting objects with prefix {Prefix}", prefix);
            throw;
        }
    }

    /// <summary>
    /// Gerar URL "pré-assinada" (local: apenas caminho relativo fake).
    /// </summary>
    public Task<string> GetPresignedDownloadUrlAsync(
        string objectKey,
        int ttlHours,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key cannot be empty", nameof(objectKey));

        // Para local, retornar fake URL
        var fakeUrl = $"/api/storage/download/{Uri.EscapeDataString(objectKey)}?ttl={ttlHours}";
        _logger.LogInformation("Generated (local) presigned URL for {ObjectKey}", objectKey);

        return Task.FromResult(fakeUrl);
    }

    /// <summary>
    /// Gerar URL "pré-assinada" de upload (local: URL fake para simulação em desenvolvimento).
    /// </summary>
    public Task<string> GetPresignedUploadUrlAsync(
        string objectKey,
        int ttlMinutes,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key cannot be empty", nameof(objectKey));

        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("Content type cannot be empty", nameof(contentType));

        var fakeUrl = $"/api/storage/upload/{Uri.EscapeDataString(objectKey)}?ttl={ttlMinutes}&contentType={Uri.EscapeDataString(contentType)}";
        _logger.LogInformation("Generated (local) presigned upload URL for {ObjectKey}", objectKey);

        return Task.FromResult(fakeUrl);
    }

    /// <summary>
    /// Obter metadados do arquivo.
    /// </summary>
    public Task<StorageObject?> GetMetadataAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key cannot be empty", nameof(objectKey));

        try
        {
            var filePath = NormalizeObjectKey(objectKey);

            if (!File.Exists(filePath))
                return Task.FromResult<StorageObject?>(null);

            var fileInfo = new FileInfo(filePath);
            var checksum = ComputeChecksum(filePath);

            return Task.FromResult<StorageObject?>(new StorageObject
            {
                ObjectKey = objectKey,
                Bucket = "local",
                ContentType = "application/octet-stream",
                SizeBytes = fileInfo.Length,
                Checksum = checksum,
                StorageProvider = Core.Enums.ObjectStorageProviderType.Local,
                StoredAt = fileInfo.LastWriteTimeUtc
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metadata for {ObjectKey}", objectKey);
            throw;
        }
    }

    public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ObjectStorageTestResult(
            Success: false,
            ConfigurationValid: false,
            BucketReachable: false,
            Errors: ["Local storage provider está desativado. Use o provider S3-compatível (MinIO)."],
            LatencyMs: 0));

    /// <summary>
    /// Normalizar object key para caminho local seguro.
    /// Evitar path traversal attacks.
    /// </summary>
    private string NormalizeObjectKey(string objectKey)
    {
        // Remover começar com / e normalizar separadores
        var normalized = objectKey.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        // Criar caminho completo
        var fullPath = Path.Combine(_basePath, normalized);

        // Verificar path traversal
        var realPath = Path.GetFullPath(fullPath);
        if (!realPath.StartsWith(Path.GetFullPath(_basePath)))
            throw new ArgumentException("Invalid object key - path traversal detected", nameof(objectKey));

        return realPath;
    }

    /// <summary>
    /// Calcular checksum simples do arquivo (MD5).
    /// </summary>
    private string ComputeChecksum(string filePath)
    {
        try
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }
        catch
        {
            return "unknown";
        }
    }
}

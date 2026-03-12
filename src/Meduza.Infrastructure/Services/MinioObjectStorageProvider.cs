using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Microsoft.Extensions.Logging;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Implementação de Object Storage com SDK MinIO para qualquer provedor S3-compatível.
/// Funciona para AWS S3, MinIO, Cloudflare R2, Oracle S3 e endpoints compatíveis.
/// </summary>
public class MinioObjectStorageProvider : IObjectStorageService
{
    private readonly IMinioClient _client;
    private readonly string _bucketName;
    private readonly string _region;
    private readonly ILogger<MinioObjectStorageProvider> _logger;

    public MinioObjectStorageProvider(
        ObjectStorageSettings settings,
        ILogger<MinioObjectStorageProvider> logger)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        if (string.IsNullOrWhiteSpace(settings.BucketName))
            throw new ArgumentException("BucketName is required", nameof(settings));

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
            throw new ArgumentException("Endpoint is required", nameof(settings));

        if (string.IsNullOrWhiteSpace(settings.AccessKey) || string.IsNullOrWhiteSpace(settings.SecretKey))
            throw new ArgumentException("AccessKey/SecretKey are required", nameof(settings));

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _bucketName = settings.BucketName;
        _region = string.IsNullOrWhiteSpace(settings.Region) ? "us-east-1" : settings.Region;

        var (endpoint, secure) = NormalizeEndpoint(settings.Endpoint);

        _client = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(settings.AccessKey, settings.SecretKey)
            .WithSSL(secure)
            .Build();

        _logger.LogInformation("MinIO S3-compatible provider initialized for endpoint {Endpoint} bucket {Bucket}", endpoint, _bucketName);
    }

    public async Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        if (content == null)
            throw new ArgumentNullException(nameof(content));

        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("ContentType cannot be empty", nameof(contentType));

        await EnsureBucketExistsAsync(cancellationToken);

        Stream uploadStream = content;
        if (!uploadStream.CanSeek)
        {
            var buffered = new MemoryStream();
            await uploadStream.CopyToAsync(buffered, cancellationToken);
            buffered.Seek(0, SeekOrigin.Begin);
            uploadStream = buffered;
        }

        if (uploadStream.CanSeek)
            uploadStream.Seek(0, SeekOrigin.Begin);

        var putArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey)
            .WithStreamData(uploadStream)
            .WithObjectSize(uploadStream.Length)
            .WithContentType(contentType);

        var result = await _client.PutObjectAsync(putArgs, cancellationToken);

        return new StorageObject
        {
            ObjectKey = objectKey,
            Bucket = _bucketName,
            ContentType = contentType,
            SizeBytes = uploadStream.Length,
            Checksum = result?.Etag,
            StorageProvider = ObjectStorageProviderType.S3Compatible,
            StoredAt = DateTime.UtcNow
        };
    }

    public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        var memoryStream = new MemoryStream();

        return DownloadAsyncInternal(objectKey, memoryStream, cancellationToken);
    }

    public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        return ExistsInternalAsync(objectKey, cancellationToken);
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        var removeArgs = new RemoveObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey);

        return _client.RemoveObjectAsync(removeArgs, cancellationToken);
    }

    public async Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix cannot be empty", nameof(prefix));

        var listArgs = new ListObjectsArgs()
            .WithBucket(_bucketName)
            .WithPrefix(prefix)
            .WithRecursive(true);

        await foreach (var obj in _client.ListObjectsEnumAsync(listArgs, cancellationToken))
        {
            var removeArgs = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(obj.Key);

            await _client.RemoveObjectAsync(removeArgs, cancellationToken);
        }
    }

    public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        if (ttlHours <= 0)
            throw new ArgumentException("ttlHours must be positive", nameof(ttlHours));

        var args = new PresignedGetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey)
            .WithExpiry(ttlHours * 3600);

        return _client.PresignedGetObjectAsync(args);
    }

    public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        if (ttlMinutes <= 0)
            throw new ArgumentException("ttlMinutes must be positive", nameof(ttlMinutes));

        var args = new PresignedPutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey)
            .WithExpiry(ttlMinutes * 60);

        return _client.PresignedPutObjectAsync(args);
    }

    public async Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        try
        {
            var statArgs = new StatObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectKey);

            var stat = await _client.StatObjectAsync(statArgs, cancellationToken);

            return new StorageObject
            {
                ObjectKey = objectKey,
                Bucket = _bucketName,
                ContentType = stat.ContentType,
                SizeBytes = stat.Size,
                Checksum = stat.ETag,
                StorageProvider = ObjectStorageProviderType.S3Compatible,
                StoredAt = stat.LastModified
            };
        }
        catch (ObjectNotFoundException)
        {
            return null;

            public async Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var existsArgs = new BucketExistsArgs().WithBucket(_bucketName);
                    var exists = await _client.BucketExistsAsync(existsArgs, cancellationToken);
                    sw.Stop();

                    return new ObjectStorageTestResult(
                        Success: exists,
                        ConfigurationValid: true,
                        BucketReachable: exists,
                        Errors: exists ? [] : [$"Bucket '{_bucketName}' não encontrado ou não acessível com as credenciais fornecidas."],
                        LatencyMs: sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogWarning(ex, "Object storage connection test failed for bucket {Bucket}", _bucketName);
                    return new ObjectStorageTestResult(
                        Success: false,
                        ConfigurationValid: true,
                        BucketReachable: false,
                        Errors: [ex.Message],
                        LatencyMs: sw.ElapsedMilliseconds);
                }
            }

        }
    }

    private async Task<Stream> DownloadAsyncInternal(string objectKey, MemoryStream target, CancellationToken cancellationToken)
    {
        var getArgs = new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey)
            .WithCallbackStream((stream, token) => stream.CopyToAsync(target, token));

        await _client.GetObjectAsync(getArgs, cancellationToken);
        target.Seek(0, SeekOrigin.Begin);
        return target;
    }

    private async Task<bool> ExistsInternalAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            var statArgs = new StatObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectKey);

            await _client.StatObjectAsync(statArgs, cancellationToken);
            return true;
        }
        catch (ObjectNotFoundException)
        {
            return false;
        }
    }

    private async Task EnsureBucketExistsAsync(CancellationToken cancellationToken)
    {
        var existsArgs = new BucketExistsArgs().WithBucket(_bucketName);
        var exists = await _client.BucketExistsAsync(existsArgs, cancellationToken);
        if (exists)
            return;

        var makeArgs = new MakeBucketArgs().WithBucket(_bucketName).WithLocation(_region);
        await _client.MakeBucketAsync(makeArgs, cancellationToken);
    }

    private static void ValidateObjectKey(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key cannot be empty", nameof(objectKey));
    }

    private static (string Endpoint, bool Secure) NormalizeEndpoint(string rawEndpoint)
    {
        if (Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var uri))
        {
            var endpoint = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return (endpoint, uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
        }

        return (rawEndpoint, true);
    }
}

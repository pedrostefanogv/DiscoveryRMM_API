using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação de Object Storage com SDK MinIO para qualquer provedor S3-compatível.
/// Funciona para AWS S3, MinIO, Cloudflare R2, Oracle S3 e endpoints compatíveis.
/// </summary>
public class MinioObjectStorageProvider : IObjectStorageService
{
    private readonly IMinioClient _client;
    // Cliente extra usado SO para assinar URLs (endpoint publico). No modo
    // interno o servidor fala com o storage por loopback, mas a URL entregue ao
    // agente precisa do host publico — a assinatura cobre o Host.
    private readonly IMinioClient _presignClient;
    // Host efetivo das URLs pre-assinadas: se for loopback, agentes/navegadores
    // nao alcancam (cada um tentaria o SEU proprio localhost).
    private readonly bool _presignEndpointIsLoopback;
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

        // UsePathStyle: o SDK MinIO escolhe path-style para endpoints nao-Amazon
        // (MinIO/RustFS/R2/Oracle) e virtual-hosted para *.amazonaws.com — o
        // settings.UsePathStyle apenas documenta a intencao.
        _client = BuildClient(settings.Endpoint, settings, _logger);
        // Modo interno (RustFS local): o servidor opera por loopback e assina as
        // URLs com o endpoint publico, que e o host alcancavel pelo agente.
        _presignClient = string.IsNullOrWhiteSpace(settings.PresignEndpoint)
            || string.Equals(settings.PresignEndpoint, settings.Endpoint, StringComparison.OrdinalIgnoreCase)
            ? _client
            : BuildClient(settings.PresignEndpoint!, settings, _logger);
        _presignEndpointIsLoopback = IsLoopbackEndpoint(settings.PresignEndpoint ?? settings.Endpoint);

        _logger.LogInformation(
            "MinIO S3-compatible provider initialized: endpoint={Endpoint} bucket={Bucket} presign={PresignEndpoint}",
            settings.Endpoint, _bucketName, settings.PresignEndpoint ?? settings.Endpoint);
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

        AssertPresignEndpointReachable();

        var args = new PresignedGetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey)
            .WithExpiry(ttlHours * 3600);

        return _presignClient.PresignedGetObjectAsync(args);
    }

    public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        if (ttlMinutes <= 0)
            throw new ArgumentException("ttlMinutes must be positive", nameof(ttlMinutes));

        AssertPresignEndpointReachable();

        var args = new PresignedPutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey)
            .WithExpiry(ttlMinutes * 60);

        return _presignClient.PresignedPutObjectAsync(args);
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
        }
    }

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

    /// <summary>
    /// Constroi um cliente MinIO para o endpoint informado (operacoes ou
    /// assinatura de URL). SslVerify=false vale apenas para HTTPS.
    /// </summary>
    private static IMinioClient BuildClient(string rawEndpoint, ObjectStorageSettings settings, ILogger logger)
    {
        var (endpoint, secure) = NormalizeEndpoint(rawEndpoint);

        HttpClient? httpClient = null;
        if (secure && !settings.SslVerify)
        {
            var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            httpClient = new HttpClient(handler);
            logger.LogWarning(
                "SSL certificate verification is DISABLED for object storage endpoint {Endpoint}. " +
                "Enable SslVerify in production environments.",
                endpoint);
        }

        var builder = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(settings.AccessKey, settings.SecretKey)
            .WithSSL(secure);

        if (httpClient is not null)
            builder = builder.WithHttpClient(httpClient);

        return builder.Build();
    }

    /// <summary>Loopback nao e alcancavel por agentes/navegadores — presign nele quebra o upload.</summary>
    private static bool IsLoopbackEndpoint(string rawEndpoint)
    {
        var (endpoint, _) = NormalizeEndpoint(rawEndpoint);
        var host = endpoint.Split(':')[0].Trim().ToLowerInvariant();
        return host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0"
            || host.StartsWith("127.", StringComparison.Ordinal);
    }

    private void AssertPresignEndpointReachable()
    {
        if (!_presignEndpointIsLoopback)
            return;

        throw new InvalidOperationException(
            "O endpoint do object storage aponta para loopback (localhost/127.0.0.1). " +
            "Agentes e navegadores nao alcancam o loopback do servidor: configure a URL PUBLICA " +
            "(ex.: https://tngplacas.com.br) no Endpoint S3 do console — no modo interno o servidor " +
            "fala com o RustFS por loopback, mas as URLs pre-assinadas usam a URL publica.");
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

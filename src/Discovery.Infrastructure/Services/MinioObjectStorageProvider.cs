using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
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
    // Credenciais + endpoint publico usados para ASSINAR LOCALMENTE as URLs de
    // parte do multipart (o SDK 7.0.0 nao pre-assina parte) e de ListParts.
    private readonly string _accessKey;
    private readonly string _secretKey;
    // Endpoint INTERNO (settings.Endpoint): usado nas chamadas multipart feitas
    // pela propria API (initiate/complete/abort/list) — loopback no modo interno.
    private readonly Uri _internalBaseUri;
    // Endpoint PUBLICO: usado SO para as URLs pre-assinadas entregues ao cliente.
    private readonly Uri _presignBaseUri;
    // Cliente HTTP das operacoes multipart assinadas localmente (initiate/
    // complete/abort/list) e do calculo de checksum. Respeita SslVerify=false,
    // senao um endpoint interno HTTPS com certificado proprio quebraria aqui
    // enquanto Upload/Download pelo SDK continuariam funcionando.
    private readonly HttpClient _presignHttpClient;
    /// <summary>
    /// Timeout das operações assinadas. O complete de um objeto montado no
    /// storage pode demorar (join + cópia interna): 5 min bastava para objetos
    /// pequenos e derrubava o de vários GiB, que ficava dependendo da retentativa
    /// idempotente.
    /// </summary>
    private static readonly TimeSpan PresignHttpTimeout = TimeSpan.FromMinutes(15);

    private static readonly HttpClient DefaultPresignHttpClient = new()
    {
        Timeout = PresignHttpTimeout
    };

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
        _presignHttpClient = !settings.SslVerify
            ? new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            })
            { Timeout = PresignHttpTimeout }
            : DefaultPresignHttpClient;
        _accessKey = settings.AccessKey;
        _secretKey = settings.SecretKey;
        _internalBaseUri = BuildPresignBaseUri(settings.Endpoint);
        _presignBaseUri = BuildPresignBaseUri(settings.PresignEndpoint ?? settings.Endpoint);

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

    public async Task DownloadToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        ArgumentNullException.ThrowIfNull(destination);

        // O callback do SDK entrega o stream do objeto; copiamos para o destino
        // em blocos — memória constante, independente do tamanho do arquivo.
        var getArgs = new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectKey)
            .WithCallbackStream((stream, token) => stream.CopyToAsync(destination, 81920, token));

        await _client.GetObjectAsync(getArgs, cancellationToken);
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

    public async Task<string> CreateMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);

        await EnsureBucketExistsAsync(cancellationToken);

        // POST /{bucket}/{key}?uploads=1 — assinado localmente (o SDK MinIO 7.0.0
        // nao expoe as APIs de multipart de forma utilizavel).
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["uploads"] = string.Empty,
        };

        var contentTypeFinal = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;

        // Content-Type e header de CONTEUDO: precisa ir no HttpContent (em
        // request.Headers o TryAddWithoutValidation descarta silenciosamente e o
        // objeto ficaria com o tipo padrao do storage).
        var initiateContent = new ByteArrayContent([]);
        initiateContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentTypeFinal);

        var (status, body) = await SendSignedAsync(
            _internalBaseUri, HttpMethod.Post, objectKey, query, initiateContent, null, cancellationToken);

        if (status != HttpStatusCode.OK)
            throw new InvalidOperationException(
                $"Falha ao iniciar o multipart upload ({(int)status} {status}): {Truncate(body, 300)}");

        var uploadId = ParseUploadId(body);
        if (string.IsNullOrWhiteSpace(uploadId))
            throw new InvalidOperationException(
                "O storage nao devolveu o UploadId do multipart: " + Truncate(body, 300));

        _logger.LogInformation(
            "Multipart upload criado: bucket={Bucket} key={Key} uploadId={UploadId}",
            _bucketName, objectKey, uploadId);

        return uploadId;
    }

    public Task<string> GetPresignedUploadPartUrlAsync(string objectKey, string uploadId, int partNumber, int ttlMinutes, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        if (string.IsNullOrWhiteSpace(uploadId))
            throw new ArgumentException("uploadId is required", nameof(uploadId));
        if (ttlMinutes <= 0)
            throw new ArgumentException("ttlMinutes must be positive", nameof(ttlMinutes));

        AssertPresignEndpointReachable();

        var url = AwsSigV4Signer.CreatePresignedPartUrl(
            _presignBaseUri, _bucketName, objectKey, uploadId, partNumber,
            _accessKey, _secretKey, _region, DateTimeOffset.UtcNow, ttlMinutes * 60);

        return Task.FromResult(url);
    }

    public async Task CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<MultipartUploadPart> parts, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        if (string.IsNullOrWhiteSpace(uploadId))
            throw new ArgumentException("uploadId is required", nameof(uploadId));
        if (parts is null || parts.Count == 0)
            throw new ArgumentException("parts is required", nameof(parts));

        var ordered = parts.OrderBy(x => x.PartNumber).ToArray();

        var xml = new StringBuilder("<CompleteMultipartUpload>");
        foreach (var part in ordered)
        {
            xml.Append("<Part><PartNumber>")
               .Append(part.PartNumber.ToString(CultureInfo.InvariantCulture))
               .Append("</PartNumber><ETag>")
               .Append(System.Security.SecurityElement.Escape(NormalizeETag(part.ETag)))
               .Append("</ETag></Part>");
        }
        xml.Append("</CompleteMultipartUpload>");

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["uploadId"] = uploadId,
        };

        var content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml");

        var (status, body) = await SendSignedAsync(
            _internalBaseUri, HttpMethod.Post, objectKey, query, content, null, cancellationToken);

        // O S3 devolve 200 no complete, mas pode devolver erro DENTRO do XML.
        if (status != HttpStatusCode.OK || body.Contains("<Error>", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Falha ao concluir o multipart upload ({(int)status} {status}): {Truncate(body, 300)}");

        _logger.LogInformation(
            "Multipart upload concluido: bucket={Bucket} key={Key} uploadId={UploadId} partes={Parts}",
            _bucketName, objectKey, uploadId, ordered.Length);
    }

    public async Task AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        if (string.IsNullOrWhiteSpace(uploadId))
            throw new ArgumentException("uploadId is required", nameof(uploadId));

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["uploadId"] = uploadId,
        };

        var (status, body) = await SendSignedAsync(
            _internalBaseUri, HttpMethod.Delete, objectKey, query, null, null, cancellationToken);

        if (status is not (HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.NotFound))
            throw new InvalidOperationException(
                $"Falha ao abortar o multipart upload ({(int)status} {status}): {Truncate(body, 300)}");

        _logger.LogInformation(
            "Multipart upload abortado: bucket={Bucket} key={Key} uploadId={UploadId}",
            _bucketName, objectKey, uploadId);
    }

    public async Task<IReadOnlyList<MultipartUploadPart>> ListMultipartUploadPartsAsync(string objectKey, string uploadId, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        if (string.IsNullOrWhiteSpace(uploadId))
            throw new ArgumentException("uploadId is required", nameof(uploadId));

        // ListParts assinado localmente (o SDK nao expoe) no endpoint INTERNO: o
        // proprio storage e a fonte de verdade de quais partes chegaram — e o que
        // permite retomar o envio de onde parou.
        //
        // O S3 pagina em 1000 partes: sem seguir IsTruncated/NextPartNumberMarker
        // um arquivo com mais de ~9,8 GiB (1000 x 10 MiB) apareceria incompleto e
        // o complete seria recusado.
        var parts = new List<MultipartUploadPart>();
        string? marker = null;

        for (var page = 0; page < 100; page++)
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["uploadId"] = uploadId,
                ["max-parts"] = "1000",
            };

            if (!string.IsNullOrEmpty(marker))
                query["part-number-marker"] = marker;

            var (status, xml) = await SendSignedAsync(
                _internalBaseUri, HttpMethod.Get, objectKey, query, null, null, cancellationToken);

            if (status != HttpStatusCode.OK)
                throw new InvalidOperationException(
                    $"Falha ao listar as partes do multipart upload ({(int)status} {status}): {Truncate(xml, 300)}");

            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            parts.AddRange(doc.Descendants(ns + "Part")
                .Select(node => new MultipartUploadPart(
                    PartNumber: int.TryParse(node.Element(ns + "PartNumber")?.Value, out var number) ? number : 0,
                    ETag: (node.Element(ns + "ETag")?.Value ?? string.Empty).Trim('"'),
                    SizeBytes: long.TryParse(node.Element(ns + "Size")?.Value, out var size) ? size : 0))
                .Where(part => part.PartNumber > 0 && !string.IsNullOrWhiteSpace(part.ETag)));

            var truncated = string.Equals(
                doc.Descendants(ns + "IsTruncated").FirstOrDefault()?.Value?.Trim(),
                "true",
                StringComparison.OrdinalIgnoreCase);

            if (!truncated)
                break;

            marker = doc.Descendants(ns + "NextPartNumberMarker").FirstOrDefault()?.Value?.Trim();
            if (string.IsNullOrEmpty(marker))
                break;
        }

        return parts.OrderBy(part => part.PartNumber).ToArray();
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

    /// <summary>
    /// Base (scheme + host + porta) do endpoint PUBLICO usado na assinatura das
    /// URLs pre-assinadas. Sem path (o path e montado por operacao).
    /// </summary>
    private static Uri BuildPresignBaseUri(string rawEndpoint)
    {
        var value = (rawEndpoint ?? string.Empty).Trim();
        if (value.Length == 0)
            value = "http://localhost";

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;

        var uri = new Uri(value, UriKind.Absolute);
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port);
        return builder.Uri;
    }

    /// <summary>
    /// Executa uma operacao do multipart direto no storage com URL assinada
    /// (SigV4, valida 300s). Usado pela propria API (initiate/complete/abort/list)
    /// no endpoint INTERNO — o cliente/painel so recebe as URLs das partes.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body)> SendSignedAsync(
        Uri baseUri,
        HttpMethod method,
        string objectKey,
        IReadOnlyDictionary<string, string>? query,
        HttpContent? content,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var url = AwsSigV4Signer.CreatePresignedObjectUrl(
            method.Method, baseUri, _bucketName, objectKey, query,
            _accessKey, _secretKey, _region, DateTimeOffset.UtcNow, 300);

        using var request = new HttpRequestMessage(method, url) { Content = content };

        if (headers is not null)
        {
            foreach (var header in headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await _presignHttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return (response.StatusCode, body);
    }

    /// <summary>UploadId do XML de InitiateMultipartUploadResult.</summary>
    private static string ParseUploadId(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return string.Empty;

        try
        {
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var uploadId = doc.Descendants(ns + "UploadId").FirstOrDefault()?.Value;
            return (uploadId ?? string.Empty).Trim();
        }
        catch (System.Xml.XmlException)
        {
            return string.Empty;
        }
    }

    /// <summary>ETag do XML/S3 sempre entre aspas no CompleteMultipartUpload.</summary>
    private static string NormalizeETag(string etag)
    {
        var value = (etag ?? string.Empty).Trim().Trim('"');
        return '"' + value + '"';
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Discovery.Core.Helpers;

/// <summary>
/// Assinatura AWS SigV4 (query string) para URLs pré-assinadas de S3/RustFS.
///
/// Existe porque o SDK MinIO .NET 7.0.0 NÃO oferece pré-assinatura por PARTE
/// (multipart): o assembly expõe NewMultipartUploadAsync, CompleteMultipartUploadAsync
/// e ListParts, mas não AbortMultipartUpload nem presign de parte. Sem assinar
/// cada parte localmente, não há como o navegador/agent enviar 10 GB direto ao
/// storage com retomada.
///
/// Implementação validada contra o vetor de teste publicado pela AWS
/// (ver AwsSigV4SignerTests.PresignedUrl_MatchesAwsDocumentedExample).
/// </summary>
public static class AwsSigV4Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Service = "s3";
    private const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>
    /// Monta a URL pré-assinada (query string) para um método/path/query.
    /// </summary>
    /// <param name="method">HTTP method (ex: "PUT", "GET", "DELETE").</param>
    /// <param name="endpoint">Endpoint público do storage (scheme + host).</param>
    /// <param name="canonicalPath">Path canônico já codificado (ex: "/bucket/key").</param>
    /// <param name="query">Parâmetros extras (ex: partNumber, uploadId).</param>
    /// <param name="accessKey">Access key.</param>
    /// <param name="secretKey">Secret key.</param>
    /// <param name="region">Região (RustFS aceita qualquer; usamos a configurada).</param>
    /// <param name="now">Instante da assinatura (UTC).</param>
    /// <param name="expiresSeconds">Validade em segundos.</param>
    public static string CreatePresignedUrl(
        string method,
        Uri endpoint,
        string canonicalPath,
        IReadOnlyDictionary<string, string>? query,
        string accessKey,
        string secretKey,
        string region,
        DateTimeOffset now,
        int expiresSeconds)
    {
        if (string.IsNullOrWhiteSpace(method))
            throw new ArgumentException("Method is required", nameof(method));
        if (endpoint is null)
            throw new ArgumentNullException(nameof(endpoint));
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Credentials are required");

        var host = endpoint.IsDefaultPort
            ? endpoint.Host
            : endpoint.Host + ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture);

        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var scope = string.Create(CultureInfo.InvariantCulture, $"{dateStamp}/{region}/{Service}/aws4_request");

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = Algorithm,
            ["X-Amz-Credential"] = accessKey + "/" + scope,
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expiresSeconds.ToString(CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "host",
        };

        if (query is not null)
        {
            foreach (var pair in query)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;
                parameters[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        var canonicalQuery = string.Join("&", parameters.Select(pair =>
            Encode(pair.Key) + "=" + Encode(pair.Value)));

        var canonicalHeaders = "host:" + host + "\n";
        const string signedHeaders = "host";

        var canonicalRequest = string.Join("\n",
            method.ToUpperInvariant(),
            canonicalPath,
            canonicalQuery,
            canonicalHeaders,
            signedHeaders,
            UnsignedPayload);

        var stringToSign = string.Join("\n",
            Algorithm,
            amzDate,
            scope,
            ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signature = ToHex(HmacSha256(SigningKey(secretKey, dateStamp, region), stringToSign));

        return endpoint.Scheme + "://" + host + canonicalPath + "?" + canonicalQuery +
               "&X-Amz-Signature=" + signature;
    }

    /// <summary>
    /// URL pré-assinada para uma OPERAÇÃO sobre o objeto (POST ?uploads=1, POST
    /// ?uploadId=…, DELETE ?uploadId=…). O path é codificado aqui — quem chama só
    /// informa bucket/key e os parâmetros extras.
    /// </summary>
    public static string CreatePresignedObjectUrl(
        string method,
        Uri endpoint,
        string bucket,
        string objectKey,
        IReadOnlyDictionary<string, string>? query,
        string accessKey,
        string secretKey,
        string region,
        DateTimeOffset now,
        int expiresSeconds)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("bucket is required", nameof(bucket));

        var path = "/" + EncodePathSegment(bucket) + "/" + EncodePath(objectKey);
        return CreatePresignedUrl(method, endpoint, path, query, accessKey, secretKey, region, now, expiresSeconds);
    }

    /// <summary>URL pré-assinada de UMA parte do upload multipart.</summary>
    public static string CreatePresignedPartUrl(
        Uri endpoint,
        string bucket,
        string objectKey,
        string uploadId,
        int partNumber,
        string accessKey,
        string secretKey,
        string region,
        DateTimeOffset now,
        int expiresSeconds)
    {
        if (partNumber is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(partNumber));

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["partNumber"] = partNumber.ToString(CultureInfo.InvariantCulture),
            ["uploadId"] = uploadId,
        };

        return CreatePresignedObjectUrl("PUT", endpoint, bucket, objectKey, query, accessKey, secretKey, region, now, expiresSeconds);
    }

    /// <summary>
    /// URL pré-assinada de ListParts (GET ?uploadId=…). O SDK MinIO 7.0.0 não
    /// expõe ListParts, então a API lista as partes por essa URL assinada — é o
    /// que permite retomar um envio de onde parou.
    /// </summary>
    public static string CreatePresignedListPartsUrl(
        Uri endpoint,
        string bucket,
        string objectKey,
        string uploadId,
        string accessKey,
        string secretKey,
        string region,
        DateTimeOffset now,
        int expiresSeconds)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
            throw new ArgumentException("uploadId is required", nameof(uploadId));

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["uploadId"] = uploadId,
        };

        return CreatePresignedObjectUrl("GET", endpoint, bucket, objectKey, query, accessKey, secretKey, region, now, expiresSeconds);
    }

    /// <summary>Codificação RFC3986 exigida pelo SigV4 em query string.</summary>
    private static string Encode(string value) => Uri.EscapeDataString(value ?? string.Empty);

    /// <summary>
    /// Path do objeto: cada segmento é codificado, mas as "/" permanecem (o S3
    /// exige que o path do recurso seja codificado sem escapar as barras).
    /// </summary>
    private static string EncodePath(string path)
        => string.Join("/", path.Split('/').Select(EncodePathSegment));

    private static string EncodePathSegment(string segment)
        => Uri.EscapeDataString(segment);

    private static byte[] SigningKey(string secretKey, string dateStamp, string region)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, Service);
        return HmacSha256(kService, "aws4_request");
    }

    private static byte[] HmacSha256(byte[] key, string data)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

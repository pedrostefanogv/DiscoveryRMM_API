using Discovery.Core.Configuration;
using Discovery.Core.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Storage S3-compatible para gravações (AWS S3, MinIO, Cloudflare R2, Wasabi).
/// </summary>
public class S3RecordingStorage
{
    private readonly RemoteAccessOptions _options;
    private readonly ILogger<S3RecordingStorage> _logger;
    private readonly HttpClient _httpClient;

    public S3RecordingStorage(
        IOptions<RemoteAccessOptions> options,
        ILogger<S3RecordingStorage> logger,
        IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("S3Recording");
    }

    public static Uri NormalizeBaseUri(string endpoint)
    {
        var raw = (endpoint ?? string.Empty).Trim();
        if (raw.Length == 0)
            raw = "https://s3.amazonaws.com";
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw;

        var uri = new Uri(raw, UriKind.Absolute);
        return new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
    }

    public static string BuildObjectUrl(RemoteAccessRecordingS3Options s3, string key)
    {
        var baseUri = NormalizeBaseUri(s3.Endpoint);
        var normalizedKey = key.TrimStart('/');
        if (s3.UsePathStyle)
            return $"{baseUri.GetLeftPart(UriPartial.Authority)}/{s3.Bucket}/{normalizedKey}";

        var virtualHost = new UriBuilder(baseUri.Scheme, $"{s3.Bucket}.{baseUri.Host}", baseUri.IsDefaultPort ? -1 : baseUri.Port).Uri;
        return $"{virtualHost.GetLeftPart(UriPartial.Authority)}/{normalizedKey}";
    }

    public static string BuildRequestUrl(RemoteAccessRecordingS3Options s3, string method, string key, int expiresSeconds = 300)
    {
        if (!string.IsNullOrWhiteSpace(s3.AccessKey) && !string.IsNullOrWhiteSpace(s3.SecretKey))
        {
            var baseUri = NormalizeBaseUri(s3.Endpoint);
            var region = string.IsNullOrWhiteSpace(s3.Region) ? "us-east-1" : s3.Region;
            return AwsSigV4Signer.CreatePresignedObjectUrl(
                method,
                baseUri,
                s3.Bucket,
                key.TrimStart('/'),
                query: null,
                s3.AccessKey,
                s3.SecretKey,
                region,
                DateTimeOffset.UtcNow,
                Math.Clamp(expiresSeconds, 1, 604800),
                usePathStyle: s3.UsePathStyle);
        }

        return BuildObjectUrl(s3, key);
    }

    public async Task<string> UploadAsync(string key, byte[] data, string contentType, CancellationToken ct = default)
    {
        var s3 = _options.Recording.S3;
        var url = BuildRequestUrl(s3, "PUT", key);

        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        using var response = await _httpClient.PutAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Gravação uploaded to S3: {Key} ({Bytes} bytes)", key, data.Length);
        return BuildObjectUrl(s3, key);
    }

    public async Task<byte[]> DownloadAsync(string key, CancellationToken ct = default)
    {
        var s3 = _options.Recording.S3;
        var url = BuildRequestUrl(s3, "GET", key);

        return await _httpClient.GetByteArrayAsync(url, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var s3 = _options.Recording.S3;
        var url = BuildRequestUrl(s3, "DELETE", key);

        using var response = await _httpClient.DeleteAsync(url, ct);
        if (response.IsSuccessStatusCode)
            _logger.LogInformation("Gravação S3 deletada: {Key}", key);
    }

    /// <summary>
    /// Gera URL pré-assinada para download temporário.
    /// </summary>
    public string PresignUrl(string key)
    {
        var s3 = _options.Recording.S3;
        var ttlSeconds = Math.Clamp((s3.PresignTtlMinutes > 0 ? s3.PresignTtlMinutes : 15) * 60, 1, 604800);

        if (!string.IsNullOrWhiteSpace(s3.AccessKey) && !string.IsNullOrWhiteSpace(s3.SecretKey))
            return BuildRequestUrl(s3, "GET", key, ttlSeconds);

        return $"{BuildObjectUrl(s3, key)}?X-Amz-Expires={ttlSeconds}";
    }
}

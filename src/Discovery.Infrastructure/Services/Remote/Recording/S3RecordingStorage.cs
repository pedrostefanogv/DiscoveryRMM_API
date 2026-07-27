using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Discovery.Core.Configuration;

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

    public async Task<string> UploadAsync(string key, byte[] data, string contentType, CancellationToken ct = default)
    {
        var s3 = _options.Recording.S3;
        var url = s3.UsePathStyle
            ? $"{s3.Endpoint}/{s3.Bucket}/{key}"
            : $"https://{s3.Bucket}.{s3.Endpoint}/{key}";

        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        var response = await _httpClient.PutAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Gravação uploaded to S3: {Key} ({Bytes} bytes)", key, data.Length);
        return url;
    }

    public async Task<byte[]> DownloadAsync(string key, CancellationToken ct = default)
    {
        var s3 = _options.Recording.S3;
        var url = s3.UsePathStyle
            ? $"{s3.Endpoint}/{s3.Bucket}/{key}"
            : $"https://{s3.Bucket}.{s3.Endpoint}/{key}";

        return await _httpClient.GetByteArrayAsync(url, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var s3 = _options.Recording.S3;
        var url = s3.UsePathStyle
            ? $"{s3.Endpoint}/{s3.Bucket}/{key}"
            : $"https://{s3.Bucket}.{s3.Endpoint}/{key}";

        var response = await _httpClient.DeleteAsync(url, ct);
        if (response.IsSuccessStatusCode)
            _logger.LogInformation("Gravação S3 deletada: {Key}", key);
    }

    /// <summary>
    /// Gera URL pré-assinada para download temporário.
    /// </summary>
    public string PresignUrl(string key)
    {
        var s3 = _options.Recording.S3;
        var expiry = DateTimeOffset.UtcNow.AddMinutes(s3.PresignTtlMinutes).ToUnixTimeSeconds();
        var url = s3.UsePathStyle
            ? $"{s3.Endpoint}/{s3.Bucket}/{key}"
            : $"https://{s3.Bucket}.{s3.Endpoint}/{key}";

        return $"{url}?X-Amz-Expires={expiry}";
    }
}

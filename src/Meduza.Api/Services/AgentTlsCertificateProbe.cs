using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Caching.Memory;

namespace Meduza.Api.Services;

public interface IAgentTlsCertificateProbe
{
    Task<string?> GetExpectedTlsCertHashAsync(CancellationToken cancellationToken = default);
}

public sealed class AgentTlsCertificateProbe : IAgentTlsCertificateProbe
{
    private const string CacheKey = "AgentTlsCertHash";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AgentTlsCertificateProbe> _logger;

    public AgentTlsCertificateProbe(
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<AgentTlsCertificateProbe> logger)
    {
        _configuration = configuration;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string?> GetExpectedTlsCertHashAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
            return cached;

        var probeUrl = _configuration.GetValue<string>("Security:AgentConnection:TlsCertificateProbeUrl");
        if (string.IsNullOrWhiteSpace(probeUrl))
            return null;

        X509Certificate2? capturedCert = null;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (cert is not null)
                {
                    capturedCert = new X509Certificate2(cert);
                }

                if (errors != SslPolicyErrors.None)
                {
                    _logger.LogWarning("TLS probe certificate validation failed for {Url}. Errors: {Errors}", probeUrl, errors);
                    return false;
                }

                return true;
            }
        };

        try
        {
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await httpClient.GetAsync(probeUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            if (capturedCert is null)
            {
                _logger.LogWarning("TLS probe did not capture a certificate for {Url}.", probeUrl);
                return null;
            }

            var hashBytes = SHA256.HashData(capturedCert.RawData);
            var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            _cache.Set(CacheKey, hash, CacheDuration);
            return hash;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "TLS probe failed for {Url}.", probeUrl);
            return null;
        }
        finally
        {
            capturedCert?.Dispose();
        }
    }
}

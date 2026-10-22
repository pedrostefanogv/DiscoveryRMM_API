using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Discovery.Core.Interfaces.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação da sonda de certificado TLS (migrada de Discovery.Api/Services
/// em 2026-10-21 para permitir o uso pelos handlers de CQRS de Infrastructure —
/// Infrastructure não pode referenciar Api).
/// </summary>
public sealed class AgentTlsCertificateProbe : IAgentTlsCertificateProbe
{
    public const string ApiCacheKey = "AgentTlsCertHash:Api";
    public const string NatsCacheKey = "AgentTlsCertHash:Nats";
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

    /// <summary>
    /// Hash do certificado que os agents veem ao falar com a API (tipicamente
    /// terminado em nginx/Cloudflare — por isso a sonda dispara contra a URL
    /// pública configurada, não contra o processo Kestrel).
    /// </summary>
    public async Task<string?> GetExpectedTlsCertHashAsync(CancellationToken cancellationToken = default)
    {
        var probeUrl = _configuration.GetValue<string>("Security:AgentConnection:TlsCertificateProbeUrl");
        if (string.IsNullOrWhiteSpace(probeUrl))
            return null;

        return await GetExpectedTlsCertHashAsync(probeUrl, ApiCacheKey, cancellationToken);
    }

    /// <summary>
    /// Hash do certificado do endpoint NATS/WSS publicado aos agents. A URL de
    /// sonda vem de <c>Security:AgentConnection:NatsTlsCertificateProbeUrl</c>;
    /// sem configuração, devolve null (agent mantém pin apenas reportado).
    /// </summary>
    public async Task<string?> GetExpectedNatsTlsCertHashAsync(CancellationToken cancellationToken = default)
    {
        var probeUrl = _configuration.GetValue<string>("Security:AgentConnection:NatsTlsCertificateProbeUrl");
        if (string.IsNullOrWhiteSpace(probeUrl))
            return null;

        return await GetExpectedTlsCertHashAsync(probeUrl, NatsCacheKey, cancellationToken);
    }

    public async Task<string?> GetExpectedTlsCertHashAsync(string probeUrl, string cacheKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(probeUrl))
            return null;

        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
            return cached;

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

            _cache.Set(cacheKey, hash, CacheDuration);
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

    public void InvalidateCache(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
            return;
        _cache.Remove(cacheKey);
    }
}

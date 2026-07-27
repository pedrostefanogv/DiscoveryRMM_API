using Discovery.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace Discovery.Api.Services;

/// <summary>
/// Emissor de credenciais TURN para WebRTC via HMAC (long-term credential mechanism).
/// Compatível com coturn (REST API auth).
/// </summary>
public class WebrtcTurnCredentialIssuer
{
    private readonly RemoteAccessOptions _options;
    private readonly ILogger<WebrtcTurnCredentialIssuer> _logger;

    public WebrtcTurnCredentialIssuer(
        IOptions<RemoteAccessOptions> options,
        ILogger<WebrtcTurnCredentialIssuer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Emite credenciais TURN temporárias para uma sessão remota.
    /// Formato compatível com coturn: username = "{expiryTimestamp}:{sessionId}"
    /// </summary>
    public (string Username, string Credential, string[] Urls, int TtlSeconds) IssueCredentials(
        string sessionId, string? clientIp = null)
    {
        var webRtc = _options.WebRtc;
        var ttl = TimeSpan.FromMinutes(webRtc.TurnCredentialTtlMinutes);
        var expiry = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
        var username = $"{expiry}:{sessionId}";

        // HMAC-SHA1 credential (coturn long-term credential)
        var credential = GenerateCredential(username);

        _logger.LogDebug("TURN credentials issued for session {SessionId}, expiry {Expiry}, urls {Urls}",
            sessionId, expiry, string.Join(",", webRtc.TurnUrls));

        return (
            Username: username,
            Credential: credential,
            Urls: webRtc.TurnUrls,
            TtlSeconds: (int)ttl.TotalSeconds
        );
    }

    private static string GenerateCredential(string username)
    {
        var key = "discovery-turn-secret"u8; // substituir por env var em produção
        var data = System.Text.Encoding.UTF8.GetBytes(username);
        var hash = HMACSHA1.HashData(key, data);
        return Convert.ToBase64String(hash);
    }
}

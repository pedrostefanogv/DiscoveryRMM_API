using Discovery.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Discovery.Api.Services;

/// <summary>
/// Emissor de JWT NATS scoped por sessão remota.
/// Cada token concede pub/sub apenas nos subjects da sessão do viewer.
/// </summary>
public class RemoteSessionJwtIssuer
{
    private readonly RemoteAccessOptions _options;
    private readonly ILogger<RemoteSessionJwtIssuer> _logger;

    public RemoteSessionJwtIssuer(
        IOptions<RemoteAccessOptions> options,
        ILogger<RemoteSessionJwtIssuer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Emite um JWT NATS scoped para uma sessão remota.
    /// O token contém claims que o NATS server usa para autorizar pub/sub.
    /// </summary>
    /// <param name="sessionId">ID da sessão remota.</param>
    /// <param name="userId">ID do usuário (viewer).</param>
    /// <param name="natsSubject">Subject NATS base da sessão.</param>
    /// <param name="permissions">Lista de permissões pub/sub (ex: "pub.remote.session.{id}.input").</param>
    public (string Jwt, string NkeySeed) IssueSessionToken(
        Guid sessionId,
        Guid userId,
        string natsSubject,
        string[] permissions)
    {
        var ttl = TimeSpan.FromMinutes(_options.DefaultTtlMinutes);
        var now = DateTime.UtcNow;

        var claims = new List<Claim>
        {
            new("sub", userId.ToString("N")),
            new("jti", Guid.NewGuid().ToString("N")),
            new("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString()),
            new("exp", new DateTimeOffset(now.Add(ttl)).ToUnixTimeSeconds().ToString()),
            new("nats_sub", natsSubject),
            new("session_id", sessionId.ToString("N")),
            new("type", "remote_session"),
        };

        // Adiciona claims de permissão pub/sub no formato esperado pelo NATS resolver
        foreach (var perm in permissions)
        {
            if (perm.StartsWith("pub."))
                claims.Add(new Claim("nats_pub", perm[4..]));
            else if (perm.StartsWith("sub."))
                claims.Add(new Claim("nats_sub_allow", perm[4..]));
        }

        // Usa chave de signing da configuração (env var / vault em produção)
        var signingKey = _options.Nats.JwtSigningKey;
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            _logger.LogWarning("NATS JWT signing key is empty — using development fallback. Configure RemoteAccess:Nats:JwtSigningKey in production.");
            signingKey = "discovery-nats-jwt-secret-dev";
        }

        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "discovery-api",
            audience: "nats-server",
            claims: claims,
            notBefore: now,
            expires: now.Add(ttl),
            signingCredentials: creds);

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        var nkeySeed = GenerateNkeySeed(sessionId);

        _logger.LogDebug(
            "NATS JWT issued for session {SessionId}, user {UserId}, permissions {PermCount}, TTL {TtlMin}min",
            sessionId, userId, permissions.Length, _options.DefaultTtlMinutes);

        return (jwt, nkeySeed);
    }

    /// <summary>
    /// Gera as permissões padrão para uma sessão remota.
    /// Viewer pode publicar em input/ack/term.in/files.req/proxy.req,
    /// e subscrever frame/term.out/files.resp/proxy.resp.
    /// </summary>
    public string[] BuildDefaultPermissions(string natsSubject)
    {
        return new[]
        {
            // Viewer publica (envia input)
            $"pub.{natsSubject}.input",
            $"pub.{natsSubject}.ack",
            $"pub.{natsSubject}.term.in",
            $"pub.{natsSubject}.files.req",
            $"pub.{natsSubject}.proxy.req",
            $"pub.{natsSubject}.signal",

            // Viewer subscreve (recebe stream)
            $"sub.{natsSubject}.frame",
            $"sub.{natsSubject}.frame.frag",
            $"sub.{natsSubject}.cursor",
            $"sub.{natsSubject}.cursor.img",
            $"sub.{natsSubject}.monitors",
            $"sub.{natsSubject}.event",
            $"sub.{natsSubject}.term.out",
            $"sub.{natsSubject}.term.ready",
            $"sub.{natsSubject}.files.ready",
            $"sub.{natsSubject}.files.resp",
            $"sub.{natsSubject}.files.progress",
            $"sub.{natsSubject}.proxy.resp",
            $"sub.{natsSubject}.signal",
        };
    }

    /// <summary>
    /// Gera um NKey seed determinístico para a sessão.
    /// Em produção, usar NKey library do NATS.
    /// </summary>
    private static string GenerateNkeySeed(Guid sessionId)
    {
        var seedBytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"seed-{sessionId:N}"));
        return Convert.ToBase64String(seedBytes)[..44]; // NKey seed tem 44 chars
    }
}

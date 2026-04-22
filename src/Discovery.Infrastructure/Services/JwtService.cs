using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Discovery.Core.Interfaces.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Serviço JWT com RS256 (chaves PEM). Se as chaves não existirem no caminho configurado,
/// gera um par RSA temporário (modo desenvolvimento).
/// </summary>
public class JwtService : IJwtService
{
    private const string ClaimMfaPending = "mfa_pending";
    private const string ClaimMfaSetup = "mfa_setup";

    private readonly RsaSecurityKey _signingKey;
    private readonly RsaSecurityKey _validationKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _accessTokenMinutes;
    private readonly int _refreshTokenDays;
    private readonly int _mfaTokenMinutes;
    private readonly int _mfaSetupTokenMinutes;

    public JwtService(IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication:Jwt");
        _issuer = section.GetValue<string>("Issuer", "Discovery")!;
        _audience = section.GetValue<string>("Audience", "Discovery")!;
        _accessTokenMinutes = section.GetValue<int>("AccessTokenExpirationMinutes", 15);
        _refreshTokenDays = section.GetValue<int>("RefreshTokenExpirationDays", 7);
        _mfaTokenMinutes = section.GetValue<int>("MfaTokenExpirationMinutes", 3);
        _mfaSetupTokenMinutes = section.GetValue<int>("MfaSetupTokenExpirationMinutes", 10);

        var privateKeyPath = section.GetValue<string>("PrivateKeyPath");
        var publicKeyPath = section.GetValue<string>("PublicKeyPath");

        (_signingKey, _validationKey) = LoadOrGenerateKeys(privateKeyPath, publicKeyPath);
    }

    private static (RsaSecurityKey signing, RsaSecurityKey validation) LoadOrGenerateKeys(
        string? privateKeyPath, string? publicKeyPath)
    {
        if (!string.IsNullOrWhiteSpace(privateKeyPath) && File.Exists(privateKeyPath)
            && !string.IsNullOrWhiteSpace(publicKeyPath) && File.Exists(publicKeyPath))
        {
            var privateRsa = RSA.Create();
            privateRsa.ImportFromPem(File.ReadAllText(privateKeyPath));

            var publicRsa = RSA.Create();
            publicRsa.ImportFromPem(File.ReadAllText(publicKeyPath));

            return (new RsaSecurityKey(privateRsa), new RsaSecurityKey(publicRsa));
        }

        // Modo desenvolvimento: gera par RSA em memória (sem persistência)
        var tempRsa = RSA.Create(2048);

        if (!string.IsNullOrWhiteSpace(privateKeyPath) && !string.IsNullOrWhiteSpace(publicKeyPath))
        {
            // Persiste as chaves geradas para consistência entre restarts
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);
                File.WriteAllText(privateKeyPath, tempRsa.ExportRSAPrivateKeyPem());
                File.WriteAllText(publicKeyPath, tempRsa.ExportSubjectPublicKeyInfoPem());
            }
            catch { /* ignora erro de escrita em dev */ }
        }

        var signingKey = new RsaSecurityKey(tempRsa);
        // Para validação, usa a mesma instância RSA (tem a chave pública embarcada)
        var validationRsa = RSA.Create();
        validationRsa.ImportRSAPublicKey(tempRsa.ExportRSAPublicKey(), out _);
        return (signingKey, new RsaSecurityKey(validationRsa));
    }

    public string GenerateAccessToken(Guid userId, Guid sessionId, IEnumerable<Claim>? extraClaims = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, sessionId.ToString()),
            new("mfa_verified", "true")
        };

        if (extraClaims != null)
            claims.AddRange(extraClaims);

        return BuildToken(claims, TimeSpan.FromMinutes(_accessTokenMinutes));
    }

    public string GenerateMfaPendingToken(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimMfaPending, "true")
        };
        return BuildToken(claims, TimeSpan.FromMinutes(_mfaTokenMinutes));
    }

    public string GenerateMfaSetupToken(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimMfaSetup, "true")
        };
        return BuildToken(claims, TimeSpan.FromMinutes(_mfaSetupTokenMinutes));
    }

    public (byte[] tokenBytes, string tokenBase64, string tokenHash) GenerateRefreshToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var b64 = Convert.ToBase64String(bytes);
        var hash = Convert.ToBase64String(SHA256.HashData(bytes));
        return (bytes, b64, hash);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        var parameters = BuildValidationParameters(validateLifetime: true);
        try
        {
            return handler.ValidateToken(token, parameters, out _);
        }
        catch
        {
            return null;
        }
    }

    public Guid? ExtractUserIdUnsafe(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        var parameters = BuildValidationParameters(validateLifetime: false);
        try
        {
            var principal = handler.ValidateToken(token, parameters, out _);
            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                      ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(sub, out var id) ? id : null;
        }
        catch
        {
            return null;
        }
    }

    private string BuildToken(IEnumerable<Claim> claims, TimeSpan lifetime)
    {
        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);
        var now = DateTime.UtcNow;

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(lifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private TokenValidationParameters BuildValidationParameters(bool validateLifetime)
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _issuer,
            ValidateAudience = true,
            ValidAudience = _audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _validationKey,
            ValidateLifetime = validateLifetime,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }
}

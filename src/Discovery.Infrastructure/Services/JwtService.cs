using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Discovery.Core.Interfaces.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Serviço JWT com RS256 (chaves PEM). Se as chaves não existirem no caminho configurado,
/// em desenvolvimento gera um par RSA temporário. Em produção, falha com exceção clara.
/// </summary>
public class JwtService : IJwtService
{
    private const string ClaimMfaPending = "mfa_pending";
    private const string ClaimMfaSetup = "mfa_setup";
    private const string ClaimStepUp = "step_up";

    private readonly RsaSecurityKey _signingKey;
    private readonly RsaSecurityKey _validationKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _accessTokenMinutes;
    private readonly int _refreshTokenDays;
    private readonly int _mfaTokenMinutes;
    private readonly int _mfaSetupTokenMinutes;
    private readonly int _stepUpTokenMinutes;

    public JwtService(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("Authentication:Jwt");
        _issuer = section.GetValue<string>("Issuer", "Discovery")!;
        _audience = section.GetValue<string>("Audience", "Discovery")!;
        _accessTokenMinutes = section.GetValue<int>("AccessTokenExpirationMinutes", 30);
        _refreshTokenDays = section.GetValue<int>("RefreshTokenExpirationDays", 7);
        _mfaTokenMinutes = section.GetValue<int>("MfaTokenExpirationMinutes", 3);
        _mfaSetupTokenMinutes = section.GetValue<int>("MfaSetupTokenExpirationMinutes", 10);
        _stepUpTokenMinutes = section.GetValue<int>("StepUpTokenExpirationMinutes", 5);

        var privateKeyPath = section.GetValue<string>("PrivateKeyPath");
        var publicKeyPath = section.GetValue<string>("PublicKeyPath");

        (_signingKey, _validationKey) = LoadOrGenerateKeys(privateKeyPath, publicKeyPath, environment.IsDevelopment());
    }

    private static (RsaSecurityKey signing, RsaSecurityKey validation) LoadOrGenerateKeys(
        string? privateKeyPath, string? publicKeyPath, bool isDevelopment)
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

        // Fail fast in production: keys must exist before startup.
        if (!isDevelopment)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(privateKeyPath)) missing.Add("Authentication:Jwt:PrivateKeyPath");
            else if (!File.Exists(privateKeyPath)) missing.Add($"PrivateKey file not found: {privateKeyPath}");
            if (string.IsNullOrWhiteSpace(publicKeyPath)) missing.Add("Authentication:Jwt:PublicKeyPath");
            else if (!File.Exists(publicKeyPath)) missing.Add($"PublicKey file not found: {publicKeyPath}");

            throw new InvalidOperationException(
                $"JWT RSA keys are required in production but are missing. " +
                $"Generate keys and configure the paths. Missing: {string.Join(", ", missing)}");
        }

        // Development mode: generate RSA pair (may persist to disk for consistency across restarts)
        var tempRsa = RSA.Create(2048);

        if (!string.IsNullOrWhiteSpace(privateKeyPath) && !string.IsNullOrWhiteSpace(publicKeyPath))
        {
            // Persist generated keys for consistency across restarts
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);
                File.WriteAllText(privateKeyPath, tempRsa.ExportRSAPrivateKeyPem());
                File.WriteAllText(publicKeyPath, tempRsa.ExportSubjectPublicKeyInfoPem());
            }
            catch { /* ignore write error in dev */ }
        }

        var signingKey = new RsaSecurityKey(tempRsa);
        // For validation, use a separate RSA instance with the public key only
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
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
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

    public string GenerateStepUpToken(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimStepUp, "true")
        };
        return BuildToken(claims, TimeSpan.FromMinutes(_stepUpTokenMinutes));
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
            // Restrições explícitas (defesa em profundidade): sem elas, algoritmos
            // aceitos ficam implícitos e um token não assinado poderia passar caso a
            // chave de validação fosse configurada de forma incorreta.
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }
}

using System.Security.Claims;

namespace Discovery.Core.Interfaces.Auth;

public interface IJwtService
{
    /// <summary>Gera um access token JWT RS256 completo (15 min).</summary>
    string GenerateAccessToken(Guid userId, Guid sessionId, IEnumerable<Claim>? extraClaims = null);

    /// <summary>Gera um token temporário de MFA pending (3 min). Claim: mfa_pending=true.</summary>
    string GenerateMfaPendingToken(Guid userId);

    /// <summary>Gera um token temporário de MFA setup (10 min). Claim: mfa_setup=true.</summary>
    string GenerateMfaSetupToken(Guid userId);

    /// <summary>Gera refresh token como bytes aleatórios; retorna (tokenBytes, tokenBase64, hash).</summary>
    (byte[] tokenBytes, string tokenBase64, string tokenHash) GenerateRefreshToken();

    /// <summary>Valida e retorna claims de qualquer JWT emitido pelo serviço.</summary>
    ClaimsPrincipal? ValidateToken(string token);

    /// <summary>Extrai o userId do claim "sub" sem validar expiração (para refresh flow).</summary>
    Guid? ExtractUserIdUnsafe(string token);
}

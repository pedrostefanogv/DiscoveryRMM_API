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

    /// <summary>
    /// Gera um token de step-up (5 min, claim step_up=true) usado para autorizar
    /// operações sensíveis da própria conta, como adicionar/remover chaves MFA.
    /// </summary>
    string GenerateStepUpToken(Guid userId);

    /// <summary>Gera refresh token como bytes aleatórios; retorna (tokenBytes, tokenBase64, hash).</summary>
    (byte[] tokenBytes, string tokenBase64, string tokenHash) GenerateRefreshToken();

    /// <summary>Valida e retorna claims de qualquer JWT emitido pelo serviço.</summary>
    ClaimsPrincipal? ValidateToken(string token);

    // ExtractUserIdUnsafe foi removido: validava a assinatura mas ignorava a expiração e
    // não possuía nenhum consumidor no código (footgun de segurança).
}

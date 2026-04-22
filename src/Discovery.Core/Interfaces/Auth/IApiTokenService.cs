using Discovery.Core.DTOs.ApiTokens;

namespace Discovery.Core.Interfaces.Auth;

public interface IApiTokenService
{
    /// <summary>
    /// Cria um novo API token para o usuário.
    /// Retorna o DTO completo incluindo a accessKey (exibida somente uma vez).
    /// </summary>
    Task<CreateApiTokenResponseDto> CreateTokenAsync(Guid userId, string name, DateTime? expiresAt);

    /// <summary>
    /// Autentica via header ApiKey.
    /// Formato esperado: "{tokenIdPublic}.{accessKey}".
    /// Retorna o userId associado ou null se inválido.
    /// </summary>
    Task<Guid?> AuthenticateAsync(string tokenIdPublicDotAccessKey);

    /// <summary>Revoga um token. Somente o dono pode revogar.</summary>
    Task<bool> RevokeAsync(Guid tokenId, Guid requestingUserId);

    Task<IEnumerable<ApiTokenSummaryDto>> GetByUserAsync(Guid userId);
}

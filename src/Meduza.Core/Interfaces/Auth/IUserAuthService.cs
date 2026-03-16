using Meduza.Core.DTOs.Auth;
using Meduza.Core.Enums.Identity;

namespace Meduza.Core.Interfaces.Auth;

public interface IUserAuthService
{
    /// <summary>
    /// Valida credenciais (login ou email + password).
    /// Retorna um mfaToken de curta duração se credenciais OK.
    /// </summary>
    Task<LoginResponseDto> LoginAsync(string loginOrEmail, string password, string? ipAddress, string? userAgent);

    /// <summary>
    /// Troca um refresh token válido por um novo par access+refresh.
    /// Rotação automática (refresh token anterior é invalidado).
    /// </summary>
    Task<TokenPairDto> RefreshAsync(string refreshToken);

    /// <summary>Revoga a sessão identificada pelo sessionId.</summary>
    Task LogoutAsync(Guid sessionId);

    /// <summary>
    /// Emite access+refresh tokens completos após MFA verificado.
    /// Chamado pelos serviços de MFA após verificação bem-sucedida.
    /// </summary>
    Task<TokenPairDto> IssueFullSessionAsync(Guid userId, bool mfaVerified, string? ipAddress, string? userAgent);

    /// <summary>
    /// Conclui o onboarding de primeiro acesso (troca de login/perfil/senha).
    /// Mantém MFA como etapa obrigatória separada.
    /// </summary>
    Task CompleteFirstAccessAsync(Guid userId, CompleteFirstAccessRequestDto dto);

    /// <summary>
    /// Retorna o status atual de onboarding de primeiro acesso para o frontend.
    /// </summary>
    Task<FirstAccessStatusDto> GetFirstAccessStatusAsync(Guid userId);

    /// <summary>
    /// Retorna a política efetiva de MFA para o usuário com base nas roles vinculadas.
    /// </summary>
    Task<RoleMfaRequirement> GetEffectiveMfaRequirementAsync(Guid userId);
}

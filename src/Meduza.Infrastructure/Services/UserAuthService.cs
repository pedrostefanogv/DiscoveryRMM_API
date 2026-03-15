using Meduza.Core.DTOs.Auth;
using Meduza.Core.Interfaces.Auth;
using Meduza.Core.Interfaces.Identity;
using Meduza.Core.Interfaces.Security;
using Meduza.Core.Entities.Security;
using Meduza.Core.Helpers;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Orquestra o fluxo de autenticação de usuários:
/// login com credenciais → emissão de mfaToken → (após MFA) emissão de sessão completa.
/// </summary>
public class UserAuthService : IUserAuthService
{
    private readonly IUserRepository _users;
    private readonly IUserSessionRepository _sessions;
    private readonly IPasswordService _passwordService;
    private readonly IJwtService _jwtService;

    public UserAuthService(
        IUserRepository users,
        IUserSessionRepository sessions,
        IPasswordService passwordService,
        IJwtService jwtService)
    {
        _users = users;
        _sessions = sessions;
        _passwordService = passwordService;
        _jwtService = jwtService;
    }

    public async Task<LoginResponseDto> LoginAsync(
        string loginOrEmail,
        string password,
        string? ipAddress,
        string? userAgent)
    {
        var user = await _users.GetByLoginOrEmailAsync(loginOrEmail);

        // Sempre executar hash para evitar timing oracle, mesmo se usuário não existe
        var dummySalt = "AAAAAAAAAAAAAAAAAAAAAA==";
        if (user == null)
        {
            _passwordService.VerifyPassword(password, dummySalt, dummySalt);
            throw new UnauthorizedAccessException("Credenciais inválidas.");
        }

        if (!user.IsActive)
            throw new UnauthorizedAccessException("Conta desativada.");

        var valid = _passwordService.VerifyPassword(password, user.PasswordSalt, user.PasswordHash);
        if (!valid)
            throw new UnauthorizedAccessException("Credenciais inválidas.");

        await _users.SetLastLoginAsync(user.Id, DateTime.UtcNow);

        // Se MFA não está ainda configurado, emite setup token (fluxo de primeiro acesso)
        if (user.MfaRequired && !user.MfaConfigured)
        {
            var setupToken = _jwtService.GenerateMfaSetupToken(user.Id);
            return new LoginResponseDto
            {
                MfaToken = setupToken,
                MfaRequired = true,
                MfaConfigured = false
            };
        }

        // MFA configurado: emite pending token para o fluxo de verificação
        var mfaToken = _jwtService.GenerateMfaPendingToken(user.Id);
        return new LoginResponseDto
        {
            MfaToken = mfaToken,
            MfaRequired = user.MfaRequired,
            MfaConfigured = user.MfaConfigured
        };
    }

    public async Task<TokenPairDto> RefreshAsync(string refreshToken)
    {
        var refreshBytes = Convert.FromBase64String(refreshToken);
        var refreshHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(refreshBytes));

        var session = await _sessions.GetByRefreshTokenHashAsync(refreshHash);
        if (session is null || !session.IsValid)
            throw new UnauthorizedAccessException("Refresh token inválido ou expirado.");

        // Rotação: revoga sessão antiga, cria nova
        await _sessions.RevokeAsync(session.Id);
        return await IssueFullSessionAsync(session.UserId, session.MfaVerified, null, null);
    }

    public async Task LogoutAsync(Guid sessionId)
    {
        await _sessions.RevokeAsync(sessionId);
    }

    public async Task<TokenPairDto> IssueFullSessionAsync(
        Guid userId,
        bool mfaVerified,
        string? ipAddress,
        string? userAgent)
    {
        var sessionId = IdGenerator.NewId();
        var (_, refreshBase64, refreshHash) = _jwtService.GenerateRefreshToken();
        var accessToken = _jwtService.GenerateAccessToken(userId, sessionId);

        var accessHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(accessToken)));

        var session = new UserSession
        {
            Id = sessionId,
            UserId = userId,
            AccessTokenHash = accessHash,
            RefreshTokenHash = refreshHash,
            MfaVerified = mfaVerified,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        await _sessions.CreateAsync(session);

        return new TokenPairDto
        {
            AccessToken = accessToken,
            RefreshToken = refreshBase64,
            ExpiresInSeconds = 900 // 15 min
        };
    }
}

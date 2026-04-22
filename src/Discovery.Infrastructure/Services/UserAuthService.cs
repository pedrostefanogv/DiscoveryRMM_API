using Discovery.Core.DTOs.Auth;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Enums.Security;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.Helpers;

namespace Discovery.Infrastructure.Services;

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
    private readonly IUserGroupRepository _userGroups;
    private readonly IRoleRepository _roles;
    private readonly IUserMfaKeyRepository _mfaKeys;

    public UserAuthService(
        IUserRepository users,
        IUserSessionRepository sessions,
        IPasswordService passwordService,
        IJwtService jwtService,
        IUserGroupRepository userGroups,
        IRoleRepository roles,
        IUserMfaKeyRepository mfaKeys)
    {
        _users = users;
        _sessions = sessions;
        _passwordService = passwordService;
        _jwtService = jwtService;
        _userGroups = userGroups;
        _roles = roles;
        _mfaKeys = mfaKeys;
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

        var roleMfaRequirement = await GetEffectiveMfaRequirementAsync(user.Id);
        var mfaKeys = (await _mfaKeys.GetActiveByUserIdAsync(user.Id)).ToList();

        var roleRequirementConfigured = roleMfaRequirement switch
        {
            RoleMfaRequirement.Totp => HasKeyType(mfaKeys, MfaKeyType.Totp),
            RoleMfaRequirement.Fido2 => HasKeyType(mfaKeys, MfaKeyType.Fido2),
            _ => true
        };

        var effectiveMfaRequired = user.MfaRequired || roleMfaRequirement != RoleMfaRequirement.None;
        var effectiveMfaConfigured = effectiveMfaRequired &&
            (roleMfaRequirement == RoleMfaRequirement.None ? user.MfaConfigured : roleRequirementConfigured);

        var firstAccessRequired = user.MustChangePassword || user.MustChangeProfile;

        // Se onboarding inicial estiver pendente, sempre retorna token de setup.
        // Isso garante troca de credenciais/perfil + setup de MFA antes da sessão completa.
        if (firstAccessRequired)
        {
            var setupToken = _jwtService.GenerateMfaSetupToken(user.Id);
            return new LoginResponseDto
            {
                MfaToken = setupToken,
                MfaRequired = effectiveMfaRequired,
                RoleMfaRequirement = roleMfaRequirement,
                MfaConfigured = effectiveMfaConfigured,
                FirstAccessRequired = true,
                MustChangePassword = user.MustChangePassword,
                MustChangeProfile = user.MustChangeProfile,
                SessionEstablished = false
            };
        }

        // Sem exigência de MFA: emite sessão completa no próprio login.
        if (!effectiveMfaRequired)
        {
            var session = await IssueFullSessionAsync(user.Id, mfaVerified: false, ipAddress, userAgent);
            return new LoginResponseDto
            {
                MfaRequired = false,
                RoleMfaRequirement = RoleMfaRequirement.None,
                MfaConfigured = user.MfaConfigured,
                FirstAccessRequired = false,
                MustChangePassword = false,
                MustChangeProfile = false,
                SessionEstablished = true,
                AccessToken = session.AccessToken,
                RefreshToken = session.RefreshToken,
                ExpiresInSeconds = session.ExpiresInSeconds
            };
        }

        // MFA exigido, mas método obrigatório não está configurado.
        if (!effectiveMfaConfigured)
        {
            var setupToken = _jwtService.GenerateMfaSetupToken(user.Id);
            return new LoginResponseDto
            {
                MfaToken = setupToken,
                MfaRequired = true,
                RoleMfaRequirement = roleMfaRequirement,
                MfaConfigured = false,
                FirstAccessRequired = false,
                MustChangePassword = false,
                MustChangeProfile = false,
                SessionEstablished = false
            };
        }

        // MFA configurado: emite pending token para o fluxo de verificação
        var mfaToken = _jwtService.GenerateMfaPendingToken(user.Id);
        return new LoginResponseDto
        {
            MfaToken = mfaToken,
            MfaRequired = true,
            RoleMfaRequirement = roleMfaRequirement,
            MfaConfigured = true,
            FirstAccessRequired = false,
            MustChangePassword = false,
            MustChangeProfile = false,
            SessionEstablished = false
        };
    }

    public async Task<RoleMfaRequirement> GetEffectiveMfaRequirementAsync(Guid userId)
    {
        var assignments = await _userGroups.GetRolesForUserAsync(userId);
        var roleIds = assignments.Select(a => a.RoleId).Distinct().ToList();

        var requirements = new List<RoleMfaRequirement>();
        foreach (var roleId in roleIds)
        {
            var role = await _roles.GetByIdAsync(roleId);
            if (role is null)
                continue;

            requirements.Add(role.MfaRequirement);
        }

        if (requirements.Contains(RoleMfaRequirement.Fido2))
            return RoleMfaRequirement.Fido2;

        if (requirements.Contains(RoleMfaRequirement.Totp))
            return RoleMfaRequirement.Totp;

        return RoleMfaRequirement.None;
    }

    public async Task CompleteFirstAccessAsync(Guid userId, CompleteFirstAccessRequestDto dto)
    {
        var user = await _users.GetByIdAsync(userId)
            ?? throw new UnauthorizedAccessException("Usuário não encontrado.");

        if (!(user.MustChangePassword || user.MustChangeProfile))
            return;

        if (!_passwordService.VerifyPassword(dto.CurrentPassword, user.PasswordSalt, user.PasswordHash))
            throw new UnauthorizedAccessException("Senha atual inválida.");

        if (!string.Equals(user.Login, dto.NewLogin, StringComparison.OrdinalIgnoreCase) &&
            await _users.ExistsByLoginAsync(dto.NewLogin))
            throw new InvalidOperationException("Login já em uso.");

        if (!string.Equals(user.Email, dto.NewEmail, StringComparison.OrdinalIgnoreCase) &&
            await _users.ExistsByEmailAsync(dto.NewEmail))
            throw new InvalidOperationException("E-mail já em uso.");

        var (isValid, reason) = _passwordService.ValidatePolicy(dto.NewPassword);
        if (!isValid)
            throw new InvalidOperationException(reason ?? "Nova senha inválida.");

        var salt = _passwordService.GenerateSalt();
        var hash = _passwordService.HashPassword(dto.NewPassword, salt);

        user.Login = dto.NewLogin.Trim();
        user.Email = dto.NewEmail.Trim();
        user.FullName = dto.NewFullName.Trim();
        user.PasswordSalt = salt;
        user.PasswordHash = hash;
        user.MustChangePassword = false;
        user.MustChangeProfile = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _users.UpdateAsync(user);
    }

    public async Task<FirstAccessStatusDto> GetFirstAccessStatusAsync(Guid userId)
    {
        var user = await _users.GetByIdAsync(userId)
            ?? throw new UnauthorizedAccessException("Usuário não encontrado.");

        return new FirstAccessStatusDto
        {
            FirstAccessRequired = user.MustChangePassword || user.MustChangeProfile,
            MustChangePassword = user.MustChangePassword,
            MustChangeProfile = user.MustChangeProfile,
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

    private static bool HasKeyType(IEnumerable<UserMfaKey> keys, MfaKeyType type)
        => keys.Any(k => k.IsActive && k.KeyType == type);
}

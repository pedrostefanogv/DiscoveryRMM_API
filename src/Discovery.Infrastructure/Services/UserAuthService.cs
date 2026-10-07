using System.Diagnostics.Metrics;
using System.Security.Claims;
using Discovery.Core.DTOs.Auth;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Enums.Security;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
    private readonly IAuthAuditLogRepository _auditLog;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UserAuthService> _logger;
    private readonly int _accessTokenSeconds;
    private readonly int _refreshTokenDays;

    /// <summary>
    /// Contador de reuso de refresh token dentro do grace period. Um pico indica ou
    /// renovação concorrente legítima (múltiplas abas) ou tentativa de reuso de token
    /// roubado — antes isso só existia como linha de log.
    /// </summary>
    private static readonly Meter AuthMeter = new("Discovery.Auth");
    private static readonly Counter<long> RefreshGraceReuseCounter =
        AuthMeter.CreateCounter<long>("auth_refresh_grace_reuse_total");

    public UserAuthService(
        IUserRepository users,
        IUserSessionRepository sessions,
        IPasswordService passwordService,
        IJwtService jwtService,
        IUserGroupRepository userGroups,
        IRoleRepository roles,
        IUserMfaKeyRepository mfaKeys,
        IAuthAuditLogRepository auditLog,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<UserAuthService> logger)
    {
        _users = users;
        _sessions = sessions;
        _passwordService = passwordService;
        _jwtService = jwtService;
        _userGroups = userGroups;
        _roles = roles;
        _mfaKeys = mfaKeys;
        _auditLog = auditLog;
        _cache = cache;
        _logger = logger;
        _accessTokenSeconds = configuration.GetValue<int>("Authentication:Jwt:AccessTokenExpirationMinutes", 30) * 60;
        _refreshTokenDays = configuration.GetValue<int>("Authentication:Jwt:RefreshTokenExpirationDays", 7);
    }

    public async Task<LoginResponseDto> LoginAsync(
        string loginOrEmail,
        string password,
        string? ipAddress,
        string? userAgent)
    {
        // Normaliza o identificador (espaços acidentais não devem invalidar o login);
        // a senha NÃO é trimada — espaços podem ser intencionais.
        var normalizedLogin = loginOrEmail?.Trim() ?? string.Empty;
        var user = await _users.GetByLoginOrEmailAsync(normalizedLogin);

        // Sempre executar hash para evitar timing oracle, mesmo se usuário não existe
        var dummySalt = "AAAAAAAAAAAAAAAAAAAAAA==";
        if (user == null)
        {
            _passwordService.VerifyPassword(password, dummySalt, dummySalt);
            await LogAuthEventAsync(null, "login_failed", false, "unknown_user", ipAddress, userAgent);
            throw new UnauthorizedAccessException("Credenciais inválidas.");
        }

        if (!user.IsActive)
        {
            await LogAuthEventAsync(user.Id, "login_failed", false, "account_disabled", ipAddress, userAgent);
            throw new UnauthorizedAccessException("Conta desativada.");
        }

        // Lockout check: se a conta está temporariamente bloqueada
        if (user.LockoutUntil.HasValue && user.LockoutUntil.Value > DateTime.UtcNow)
        {
            await LogAuthEventAsync(user.Id, "login_failed", false, "account_locked", ipAddress, userAgent,
                $"LockoutUntil={user.LockoutUntil:O}, FailedAttempts={user.FailedLoginAttempts}");
            var remainingSeconds = (int)(user.LockoutUntil.Value - DateTime.UtcNow).TotalSeconds;
            throw new UnauthorizedAccessException(
                $"Conta temporariamente bloqueada. Tente novamente em {remainingSeconds} segundos.");
        }

        var valid = _passwordService.VerifyPassword(password, user.PasswordSalt, user.PasswordHash);
        if (!valid)
        {
            await RecordFailedLoginAttemptAsync(user);
            await LogAuthEventAsync(user.Id, "login_failed", false, "invalid_password", ipAddress, userAgent,
                $"LockoutUntil={user.LockoutUntil:O}, FailedAttempts={user.FailedLoginAttempts}");
            throw new UnauthorizedAccessException("Credenciais inválidas.");
        }

        // Reset lockout on success
        if (user.FailedLoginAttempts > 0 || user.LockoutUntil.HasValue)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutUntil = null;
            await _users.UpdateAsync(user);
        }

        await _users.SetLastLoginAsync(user.Id, DateTime.UtcNow);
        await LogAuthEventAsync(user.Id, "login_success", true, null, ipAddress, userAgent);

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

        if (roleIds.Count == 0)
            return RoleMfaRequirement.None;

        var roles = await _roles.GetByIdsAsync(roleIds);
        var requirements = roles.Select(r => r.MfaRequirement).ToList();

        if (requirements.Contains(RoleMfaRequirement.Fido2))
            return RoleMfaRequirement.Fido2;

        if (requirements.Contains(RoleMfaRequirement.Totp))
            return RoleMfaRequirement.Totp;

        return RoleMfaRequirement.None;
    }

    public async Task<(bool CanUse, string? Reason)> CanUseApiTokensAsync(Guid userId)
    {
        var user = await _users.GetByIdAsync(userId);
        if (user is null)
            return (false, "Usuario nao encontrado.");

        if (!user.IsActive)
            return (false, "Usuario inativo.");

        var requirement = await GetEffectiveMfaRequirementAsync(user.Id);
        if (requirement == RoleMfaRequirement.None)
            return (true, null);

        var mfaKeys = (await _mfaKeys.GetActiveByUserIdAsync(user.Id)).ToList();
        var configured = requirement switch
        {
            RoleMfaRequirement.Totp => mfaKeys.Any(k => k.KeyType == MfaKeyType.Totp),
            RoleMfaRequirement.Fido2 => mfaKeys.Any(k => k.KeyType == MfaKeyType.Fido2),
            _ => true
        };

        if (!configured)
            return (false, "MFA exigido pelas roles do usuario, mas nao configurado. Configure o MFA para usar API tokens.");

        return (true, null);
    }

    public async Task CompleteFirstAccessAsync(Guid userId, CompleteFirstAccessRequestDto dto)
    {
        var user = await _users.GetByIdAsync(userId)
            ?? throw new UnauthorizedAccessException("Usuário não encontrado.");

        if (!(user.MustChangePassword || user.MustChangeProfile))
            return;

        if (!_passwordService.VerifyPassword(dto.CurrentPassword, user.PasswordSalt, user.PasswordHash))
            throw new UnauthorizedAccessException("Senha atual inválida.");

        // O perfil só é validado/alterado quando o onboarding exigiu troca de perfil
        // (MustChangeProfile). No reset administrativo de senha o usuário é obrigado a
        // trocar apenas a senha e não deve re-digitar login/e-mail/nome.
        if (user.MustChangeProfile)
        {
            if (string.IsNullOrWhiteSpace(dto.NewLogin)
                || string.IsNullOrWhiteSpace(dto.NewEmail)
                || string.IsNullOrWhiteSpace(dto.NewFullName))
                throw new InvalidOperationException("Informe login, e-mail e nome completo.");

            if (!string.Equals(user.Login, dto.NewLogin, StringComparison.OrdinalIgnoreCase) &&
                await _users.ExistsByLoginAsync(dto.NewLogin))
                throw new InvalidOperationException("Login já em uso.");

            if (!string.Equals(user.Email, dto.NewEmail, StringComparison.OrdinalIgnoreCase) &&
                await _users.ExistsByEmailAsync(dto.NewEmail))
                throw new InvalidOperationException("E-mail já em uso.");
        }

        var (isValid, reason) = _passwordService.ValidatePolicy(dto.NewPassword);
        if (!isValid)
            throw new InvalidOperationException(reason ?? "Nova senha inválida.");

        var salt = _passwordService.GenerateSalt();
        var hash = _passwordService.HashPassword(dto.NewPassword, salt);

        if (user.MustChangeProfile)
        {
            user.Login = dto.NewLogin.Trim();
            user.Email = dto.NewEmail.Trim();
            user.FullName = dto.NewFullName.Trim();
        }

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
            MfaConfigured = user.MfaConfigured,
            Login = user.Login,
            Email = user.Email,
            FullName = user.FullName
        };
    }

    public async Task<TokenPairDto> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null)
    {
        // Validação: token vazio ou nulo
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            _logger.LogWarning("[Refresh] Token vazio ou nulo recebido");
            throw new UnauthorizedAccessException("Refresh token inválido ou expirado.");
        }

        // Decodificação segura do Base64
        byte[] refreshBytes;
        try
        {
            refreshBytes = Convert.FromBase64String(refreshToken);
        }
        catch (FormatException)
        {
            _logger.LogWarning("[Refresh] Token Base64 inválido: prefixo={Prefix}",
                refreshToken.Length > 10 ? refreshToken[..10] : refreshToken);
            throw new UnauthorizedAccessException("Refresh token inválido ou expirado.");
        }

        var refreshHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(refreshBytes));

        // Busca incluindo sessões revogadas dentro do grace period
        var session = await _sessions.GetByRefreshTokenHashWithGracePeriodAsync(refreshHash);
        if (session is null)
        {
            _logger.LogWarning("[Refresh] Sessão não encontrada para o hash do refresh token");
            throw new UnauthorizedAccessException("Refresh token inválido ou expirado.");
        }

        // Sessão revogada fora do grace period
        if (session.IsRevoked && !session.IsWithinRefreshGracePeriod)
        {
            _logger.LogWarning(
                "[Refresh] Sessão {SessionId} revogada fora do grace (RevokedAt={RevokedAt}, GraceUntil={GraceUntil}, Now={Now}, UserId={UserId})",
                session.Id, session.RevokedAt, session.RefreshTokenGracePeriodUntil, DateTime.UtcNow, session.UserId);
            throw new UnauthorizedAccessException("Refresh token inválido ou expirado.");
        }

        // Sessão expirada
        if (session.IsExpired)
        {
            _logger.LogWarning(
                "[Refresh] Sessão {SessionId} expirada (ExpiresAt={ExpiresAt}, Now={Now}, UserId={UserId})",
                session.Id, session.ExpiresAt, DateTime.UtcNow, session.UserId);
            throw new UnauthorizedAccessException("Refresh token inválido ou expirado.");
        }

        bool isWithinGrace = session.IsRevoked && session.IsWithinRefreshGracePeriod;
        if (isWithinGrace)
        {
            RefreshGraceReuseCounter.Add(1);
            _logger.LogInformation(
                "[Refresh] Sessão {SessionId} já revogada mas dentro do grace period — reutilizando (UserId={UserId})",
                session.Id, session.UserId);
        }

        // Validação: o usuário precisa existir e estar ativo. Sem isto, contas
        // desativadas continuam renovando sessão até a expiração do refresh token (7 dias).
        var refreshUser = await _users.GetByIdAsync(session.UserId);
        if (refreshUser is null || !refreshUser.IsActive)
        {
            await _sessions.RevokeAsync(session.Id);
            _logger.LogWarning(
                "[Refresh] Sessão {SessionId} encerrada: usuário {UserId} inexistente ou inativo",
                session.Id, session.UserId);
            throw new UnauthorizedAccessException("Sessão encerrada. Faça login novamente.");
        }

        // Rotação com grace period de 5 minutos
        if (!session.IsRevoked)
        {
            await _sessions.RevokeWithGracePeriodAsync(session.Id, TimeSpan.FromMinutes(5));
        }

        // Preserva a origem da requisição na auditoria da nova sessão (antes gravava null).
        return await IssueFullSessionAsync(session.UserId, session.MfaVerified, ipAddress, userAgent);
    }

    public async Task LogoutAsync(Guid sessionId)
    {
        await _sessions.RevokeAsync(sessionId);
    }

    public async Task<StepUpTokenDto> CreateStepUpTokenAsync(Guid userId, string password)
    {
        var user = await _users.GetByIdAsync(userId)
            ?? throw new UnauthorizedAccessException("Usuário não encontrado.");

        if (!user.IsActive)
            throw new UnauthorizedAccessException("Conta desativada.");

        if (string.IsNullOrEmpty(password)
            || !_passwordService.VerifyPassword(password, user.PasswordSalt, user.PasswordHash))
        {
            await LogAuthEventAsync(userId, "step_up_failed", false, "invalid_password", null, null);
            throw new UnauthorizedAccessException("Senha incorreta.");
        }

        await LogAuthEventAsync(userId, "step_up_granted", true, null, null, null);

        return new StepUpTokenDto
        {
            StepUpToken = _jwtService.GenerateStepUpToken(userId),
            ExpiresInSeconds = 5 * 60
        };
    }

    public async Task<TokenPairDto> IssueFullSessionAsync(
        Guid userId,
        bool mfaVerified,
        string? ipAddress,
        string? userAgent)
    {
        var sessionId = IdGenerator.NewId();
        var (_, refreshBase64, refreshHash) = _jwtService.GenerateRefreshToken();

        // Inclui o username (login) como claim para que o autor de ações
        // (notas, custom fields, tickets, etc.) possa ser resolvido da identidade
        // autenticada e não de campos enviados pelo cliente.
        var loginClaim = await ResolveUsernameClaimAsync(userId);

        var extraClaims = new List<Claim>();
        if (loginClaim is not null)
            extraClaims.Add(loginClaim);

        // Claims de autorização: sem elas o console web não tinha como aplicar os gates
        // de permissão (authorization.ts caía em "allow by default" e liberava tudo).
        extraClaims.AddRange(await BuildAuthorizationClaimsAsync(userId));

        var accessToken = _jwtService.GenerateAccessToken(userId, sessionId, extraClaims);

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
            ExpiresAt = DateTime.UtcNow.AddDays(_refreshTokenDays)
        };

        await _sessions.CreateAsync(session);
        await LogAuthEventAsync(userId, "session_created", true, null, ipAddress, userAgent,
            $"MfaVerified={mfaVerified}");

        return new TokenPairDto
        {
            AccessToken = accessToken,
            RefreshToken = refreshBase64,
            ExpiresInSeconds = _accessTokenSeconds
        };
    }

    private static bool HasKeyType(IEnumerable<UserMfaKey> keys, MfaKeyType type)
        => keys.Any(k => k.IsActive && k.KeyType == type);

    private const string PermissionClaimType = "permissions";
    private const string RoleClaimType = "roles";

    /// <summary>
    /// Resolve as permissões efetivas ("Recurso.Ação") e os nomes das roles do usuário para
    /// o access token. Em caso de falha emite claims vazias (fail-closed): é preferível um
    /// usuário sem menu por alguns minutos do que um cliente autorizando tudo.
    /// </summary>
    private async Task<IReadOnlyList<Claim>> BuildAuthorizationClaimsAsync(Guid userId)
    {
        // Cache curto: o login/refresh não precisa recarregar permissões a cada renovação
        // (eram 3 queries por refresh). Alterações de permissão passam a valer em <= 60s,
        // bem abaixo da vida do access token.
        var cacheKey = $"authz-claims:{userId:N}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<Claim>? cached) && cached is not null)
            return cached;

        try
        {
            var assignments = await _userGroups.GetRolesWithPermissionsForUserAsync(userId);
            var totalActions = Enum.GetValues<ActionType>().Length;

            var permissions = new List<string>();
            foreach (var group in assignments
                         .SelectMany(a => a.Permissions)
                         .DistinctBy(p => (p.ResourceType, p.ActionType))
                         .GroupBy(p => p.ResourceType))
            {
                var actions = group.Select(p => p.ActionType).Distinct().ToList();

                // Compressão: recurso com todas as ações vira "Recurso.*" — reduz o tamanho
                // do access token (um Admin cheio chega a ~80 permissões).
                if (actions.Count >= totalActions)
                    permissions.Add($"{group.Key}.*");
                else
                    permissions.AddRange(actions.Select(action => $"{group.Key}.{action}"));
            }

            permissions.Sort(StringComparer.Ordinal);

            var roleIds = (await _userGroups.GetRolesForUserAsync(userId))
                .Select(a => a.RoleId)
                .Distinct()
                .ToList();

            var roleNames = roleIds.Count == 0
                ? []
                : (await _roles.GetByIdsAsync(roleIds))
                    .Select(r => r.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            IReadOnlyList<Claim> claims =
            [
                new Claim(PermissionClaimType, string.Join(' ', permissions)),
                new Claim(RoleClaimType, string.Join(' ', roleNames))
            ];

            _cache.Set(cacheKey, claims, TimeSpan.FromSeconds(60));
            return claims;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao resolver permissões/roles do usuário {UserId}; emitindo claims vazias.", userId);

            return
            [
                new Claim(PermissionClaimType, string.Empty),
                new Claim(RoleClaimType, string.Empty)
            ];
        }
    }

    /// <summary>
    /// Resolve o username (login) de um usuário para incluir como claim no access token.
    /// </summary>
    private async Task<Claim?> ResolveUsernameClaimAsync(Guid userId)
    {
        var user = await _users.GetByIdAsync(userId);
        if (user is null || string.IsNullOrWhiteSpace(user.Login))
        {
            _logger.LogWarning(
                "ResolveUsernameClaimAsync: usuário {UserId} não encontrado ou sem login — pulando claim de username.",
                userId);
            return null;
        }
        return new Claim("unique_name", user.Login);
    }

    /// <summary>
    /// Registra falha de login e aplica lockout progressivo:
    /// - 5 falhas → 60 segundos
    /// - 10 falhas → 300 segundos (5 min)
    /// - 20+ falhas → 1800 segundos (30 min)
    /// </summary>
    private async Task RecordFailedLoginAttemptAsync(User user)
    {
        user.FailedLoginAttempts++;
        user.LockoutUntil = user.FailedLoginAttempts switch
        {
            >= 20 => DateTime.UtcNow.AddSeconds(1800),
            >= 10 => DateTime.UtcNow.AddSeconds(300),
            >= 5 => DateTime.UtcNow.AddSeconds(60),
            _ => null
        };
        await _users.UpdateAsync(user);
    }

    private async Task LogAuthEventAsync(
        Guid? userId,
        string eventType,
        bool success,
        string? failureReason,
        string? ipAddress,
        string? userAgent,
        string? detail = null)
    {
        try
        {
            await _auditLog.AddAsync(new AuthAuditLog
            {
                Id = IdGenerator.NewId(),
                UserId = userId,
                EventType = eventType,
                Success = success,
                FailureReason = failureReason,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                Detail = detail,
                OccurredAt = DateTime.UtcNow
            });
        }
        catch
        {
            // Não falha o login por erro de auditoria
        }
    }

    /// <summary>Desbloqueia manualmente uma conta (reset de lockout).</summary>
    public async Task UnlockAsync(Guid userId)
    {
        var user = await _users.GetByIdAsync(userId)
            ?? throw new InvalidOperationException("Usuário não encontrado.");

        // Libera os dois bloqueios: senha e segundo fator. Antes só o de senha era
        // limpo, então "desbloquear" podia manter o usuário travado no MFA.
        user.FailedLoginAttempts = 0;
        user.LockoutUntil = null;
        user.MfaFailedAttempts = 0;
        user.MfaLockoutUntil = null;
        user.UpdatedAt = DateTime.UtcNow;
        await _users.UpdateAsync(user);
    }
}

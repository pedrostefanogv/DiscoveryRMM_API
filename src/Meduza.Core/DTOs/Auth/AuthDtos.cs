using Meduza.Core.Enums.Identity;

namespace Meduza.Core.DTOs.Auth;

public class LoginRequestDto
{
    /// <summary>Login (username) ou email do usuário.</summary>
    public string LoginOrEmail { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginResponseDto
{
    /// <summary>
    /// JWT de curta duração (3min) com claim mfa_pending=true.
    /// Deve ser enviado no header Authorization: Bearer {mfaToken} nos endpoints de MFA.
    /// </summary>
    public string MfaToken { get; set; } = string.Empty;

    public bool MfaRequired { get; set; }

    /// <summary>
    /// Política de MFA derivada das roles do usuário.
    /// </summary>
    public RoleMfaRequirement RoleMfaRequirement { get; set; } = RoleMfaRequirement.None;

    /// <summary>
    /// false = primeiro login, usuário ainda não cadastrou chave FIDO2.
    /// Neste caso, MfaToken tem claim mfa_setup=true e deve ser usado para registrar a primeira chave.
    /// </summary>
    public bool MfaConfigured { get; set; }

    /// <summary>
    /// Indica se o usuário ainda precisa concluir onboarding de primeiro acesso
    /// (troca de login/perfil/senha e setup de MFA).
    /// </summary>
    public bool FirstAccessRequired { get; set; }

    public bool MustChangePassword { get; set; }
    public bool MustChangeProfile { get; set; }

    /// <summary>
    /// true quando o login já retornou a sessão completa sem exigir etapa MFA.
    /// </summary>
    public bool SessionEstablished { get; set; }

    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public int? ExpiresInSeconds { get; set; }
}

public class CompleteFirstAccessRequestDto
{
    public string NewLogin { get; set; } = string.Empty;
    public string NewEmail { get; set; } = string.Empty;
    public string NewFullName { get; set; } = string.Empty;
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class FirstAccessStatusDto
{
    public bool FirstAccessRequired { get; set; }
    public bool MustChangePassword { get; set; }
    public bool MustChangeProfile { get; set; }
    public bool MfaRequired { get; set; }
    public bool MfaConfigured { get; set; }
}

public class TokenPairDto
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public int ExpiresInSeconds { get; set; }
}

public class RefreshTokenRequestDto
{
    public string RefreshToken { get; set; } = string.Empty;
}

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
    /// false = primeiro login, usuário ainda não cadastrou chave FIDO2.
    /// Neste caso, MfaToken tem claim mfa_setup=true e deve ser usado para registrar a primeira chave.
    /// </summary>
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

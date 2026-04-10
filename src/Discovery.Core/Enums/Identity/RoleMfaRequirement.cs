namespace Discovery.Core.Enums.Identity;

/// <summary>
/// Política de MFA exigida por uma role.
/// </summary>
public enum RoleMfaRequirement
{
    /// <summary>Não exige MFA para usuários vinculados à role.</summary>
    None,

    /// <summary>Exige MFA via código OTP (TOTP).</summary>
    Totp,

    /// <summary>Exige MFA via chave de segurança/passkey (FIDO2).</summary>
    Fido2
}
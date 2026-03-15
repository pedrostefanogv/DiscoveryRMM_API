namespace Meduza.Core.Enums.Security;

public enum MfaKeyType
{
    /// <summary>Chave de hardware / passkey (FIDO2 / WebAuthn). Método obrigatório primário.</summary>
    Fido2,

    /// <summary>Segredo TOTP (OTP por tempo). Preparado para uso futuro.</summary>
    Totp
}

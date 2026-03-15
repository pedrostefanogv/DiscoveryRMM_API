using Meduza.Core.Enums.Security;

namespace Meduza.Core.Entities.Security;

/// <summary>
/// Chave MFA registrada por um usuário.
/// Para FIDO2: armazena CredentialId, PublicKey, SignCount.
/// Para TOTP (preparado): armazena OtpSecret criptografado.
/// </summary>
public class UserMfaKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public MfaKeyType KeyType { get; set; }

    /// <summary>Nome amigável dado pelo usuário (ex: "YubiKey 5C", "iPhone Passkey").</summary>
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    // ─── FIDO2 fields ───────────────────────────────────────────────────────
    /// <summary>FIDO2 credential ID (raw bytes, Base64url-encoded para storage).</summary>
    public string? CredentialIdBase64 { get; set; }

    /// <summary>COSE public key bytes (Base64-encoded).</summary>
    public string? PublicKeyBase64 { get; set; }

    /// <summary>Contador de assinaturas para detectar clonagem de chave.</summary>
    public uint SignCount { get; set; }

    /// <summary>AAGUID do autenticador (identifica o modelo de hardware).</summary>
    public string? AaguidBase64 { get; set; }

    /// <summary>User handle FIDO2 (Base64-encoded).</summary>
    public string? UserHandleBase64 { get; set; }

    // ─── TOTP fields (preparado) ─────────────────────────────────────────────
    /// <summary>Segredo TOTP criptografado (AES-GCM via DataProtection). Null se KeyType != Totp.</summary>
    public string? OtpSecretEncrypted { get; set; }

    /// <summary>Hashes dos códigos de backup (Argon2id). Null se não configurado.</summary>
    public string[]? BackupCodeHashes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

namespace Meduza.Core.Interfaces.Auth;

/// <summary>
/// Serviço TOTP (RFC 6238). Preparado para uso futuro.
/// Não faz parte do fluxo obrigatório de MFA (que é FIDO2).
/// </summary>
public interface IOtpService
{
    /// <summary>Gera um novo segredo TOTP (20 bytes) e retorna Base32 e URI de QR code.</summary>
    (string secretBase32, string qrCodeUri) GenerateSecret(string issuer, string accountName);

    /// <summary>Valida um código TOTP de 6 dígitos. Janela de ±1 step (30s).</summary>
    bool ValidateTotp(string secretBase32, string code);

    /// <summary>Gera N códigos de backup de uso único. Retorna (plaintext, hashes).</summary>
    (IEnumerable<string> plaintextCodes, IEnumerable<string> hashedCodes) GenerateBackupCodes(int count = 8);

    /// <summary>Verifica se um código de backup é válido (consome o código).</summary>
    bool VerifyBackupCode(string code, IEnumerable<string> storedHashes, out string? matchedHash);
}

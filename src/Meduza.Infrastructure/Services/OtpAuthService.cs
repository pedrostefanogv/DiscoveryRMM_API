using OtpNet;
using Meduza.Core.Interfaces.Auth;
using System.Security.Cryptography;
using System.Text;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Serviço TOTP (RFC 6238) preparado para uso futuro com OtpNet.
/// Não faz parte do fluxo obrigatório de MFA (que usa FIDO2).
/// </summary>
public class OtpAuthService : IOtpService
{
    public (string secretBase32, string qrCodeUri) GenerateSecret(string issuer, string accountName)
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        var base32 = Base32Encoding.ToString(key);

        var issuerEncoded = Uri.EscapeDataString(issuer);
        var accountEncoded = Uri.EscapeDataString(accountName);
        var secretEncoded = Uri.EscapeDataString(base32);

        var qrUri = $"otpauth://totp/{issuerEncoded}:{accountEncoded}?secret={secretEncoded}&issuer={issuerEncoded}&algorithm=SHA1&digits=6&period=30";

        return (base32, qrUri);
    }

    public bool ValidateTotp(string secretBase32, string code)
    {
        if (string.IsNullOrEmpty(secretBase32) || string.IsNullOrEmpty(code))
            return false;

        try
        {
            var key = Base32Encoding.ToBytes(secretBase32);
            var totp = new Totp(key);
            return totp.VerifyTotp(DateTime.UtcNow, code, out _, new VerificationWindow(2, 2));
        }
        catch
        {
            return false;
        }
    }

    public (IEnumerable<string> plaintextCodes, IEnumerable<string> hashedCodes) GenerateBackupCodes(int count = 8)
    {
        var plaintextCodes = new List<string>();
        var hashedCodes = new List<string>();

        for (int i = 0; i < count; i++)
        {
            var bytes = new byte[5];
            RandomNumberGenerator.Fill(bytes);
            // Formato: XXXX-XXXX (8 hex digits)
            var code = Convert.ToHexString(bytes)[..8].ToLowerInvariant();
            var readableCode = code[..4] + "-" + code[4..];
            plaintextCodes.Add(readableCode);
            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(readableCode)));
            hashedCodes.Add(hash);
        }

        return (plaintextCodes, hashedCodes);
    }

    public bool VerifyBackupCode(string code, IEnumerable<string> storedHashes, out string? matchedHash)
    {
        var codeHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToLowerInvariant())));
        matchedHash = storedHashes.FirstOrDefault(h =>
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(h),
                Convert.FromBase64String(codeHash)));
        return matchedHash != null;
    }
}

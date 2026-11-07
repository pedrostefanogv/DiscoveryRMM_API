using OtpNet;
using Discovery.Core.Interfaces.Auth;
using System.Security.Cryptography;
using System.Text;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Serviço TOTP (RFC 6238) preparado para uso futuro com OtpNet.
/// Não faz parte do fluxo obrigatório de MFA (que usa FIDO2).
/// </summary>
public class OtpAuthService : IOtpService
{
    private readonly IPasswordService _passwordService;

    /// <summary>
    /// Os códigos de backup usam o MESMO KDF das senhas (Argon2id com salt por
    /// código), em vez do SHA-256 sem salt usado antes.
    /// </summary>
    public OtpAuthService(IPasswordService passwordService)
        => _passwordService = passwordService;

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

    public bool TryValidateTotp(string secretBase32, string code, long? minStepExclusive, out long matchedStep)
    {
        matchedStep = 0;

        if (string.IsNullOrEmpty(secretBase32) || string.IsNullOrEmpty(code))
            return false;

        try
        {
            var key = Base32Encoding.ToBytes(secretBase32);
            var totp = new Totp(key);

            if (!totp.VerifyTotp(DateTime.UtcNow, code, out var step, new VerificationWindow(2, 2)))
                return false;

            // Replay: o mesmo código (ou um código mais antigo) já foi consumido.
            if (minStepExclusive.HasValue && step <= minStepExclusive.Value)
                return false;

            matchedStep = step;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gera códigos de backup de uso único.
    ///
    /// Os códigos são curtos por natureza (precisam ser digitáveis), então o hash
    /// precisa compensar a entropia limitada com custo computacional. Antes era
    /// SHA-256 puro e SEM SALT: um vazamento do banco permitia brute-force offline
    /// dos códigos (o espaço é de 40 bits, trivialmente enumerável em GPU).
    ///
    /// Agora usa <see cref="IPasswordService"/> — o mesmo Argon2id das senhas — com
    /// salt por código. O formato armazenado é <c>salt:hash</c>, que se
    /// auto-descreve para a verificação.
    /// </summary>
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

            var salt = _passwordService.GenerateSalt();
            var hash = _passwordService.HashPassword(NormalizeCode(readableCode), salt);
            hashedCodes.Add($"{salt}:{hash}");
        }

        return (plaintextCodes, hashedCodes);
    }

    public bool VerifyBackupCode(string code, IEnumerable<string> storedHashes, out string? matchedHash)
    {
        matchedHash = null;
        var normalized = NormalizeCode(code);

        foreach (var stored in storedHashes)
        {
            if (string.IsNullOrWhiteSpace(stored))
                continue;

            // Formato novo: "salt:hash" (Argon2id + salt por código).
            var separator = stored.IndexOf(':');
            if (separator <= 0)
                continue;

            var salt = stored[..separator];
            var expected = stored[(separator + 1)..];

            if (_passwordService.VerifyPassword(normalized, salt, expected))
            {
                matchedHash = stored;
                return true;
            }
        }

        return false;
    }

    /// <summary>Normaliza para comparação: sem espaços/hífens e minúsculo.</summary>
    private static string NormalizeCode(string code)
        => code.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace(" ", string.Empty);
}

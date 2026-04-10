using System.Security.Cryptography;
using System.Text;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services;

public sealed class SecretProtector : ISecretProtector
{
    private const string Prefix = "enc:v1:";

    private readonly ILogger<SecretProtector> _logger;
    private readonly string _keyId;
    private readonly byte[]? _masterKey;

    public SecretProtector(IOptions<SecretEncryptionOptions> options, ILogger<SecretProtector> logger)
    {
        _logger = logger;
        var config = options.Value;

        _keyId = string.IsNullOrWhiteSpace(config.KeyId) ? "v1" : config.KeyId.Trim();
        _masterKey = ResolveMasterKey(config.MasterKeyBase64);

        IsEnabled = config.Enabled && _masterKey is { Length: 32 };
        if (config.Enabled && !IsEnabled)
        {
            _logger.LogWarning("Secret encryption está habilitada, mas MasterKeyBase64 não é válida (esperado 32 bytes em Base64). O serviço ficará em modo compatível sem criptografia.");
        }
    }

    public bool IsEnabled { get; }

    public bool IsProtected(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            return plaintext;

        if (IsProtected(plaintext))
            return plaintext;

        if (!IsEnabled || _masterKey is null)
            return plaintext;

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        var aad = Encoding.UTF8.GetBytes($"discovery:{_keyId}");

        using var aes = new AesGcm(_masterKey, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag, aad);

        var payload = Convert.ToBase64String(Combine(nonce, tag, cipher));
        return $"{Prefix}{_keyId}:{payload}";
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            return protectedValue;

        if (!IsProtected(protectedValue))
            return protectedValue;

        if (!IsEnabled || _masterKey is null)
            throw new InvalidOperationException("Secret encryption está desabilitada; não é possível descriptografar payload protegido.");

        var marker = protectedValue.IndexOf(':', Prefix.Length);
        if (marker < 0)
            throw new InvalidOperationException("Formato de segredo criptografado inválido.");

        var keyId = protectedValue[Prefix.Length..marker];
        var base64Payload = protectedValue[(marker + 1)..];
        if (!string.Equals(keyId, _keyId, StringComparison.Ordinal))
            throw new InvalidOperationException($"KeyId '{keyId}' não suportado pela chave ativa.");

        var blob = Convert.FromBase64String(base64Payload);
        if (blob.Length < 12 + 16)
            throw new InvalidOperationException("Payload criptografado inválido.");

        var nonce = blob[..12];
        var tag = blob[12..28];
        var cipher = blob[28..];
        var plain = new byte[cipher.Length];
        var aad = Encoding.UTF8.GetBytes($"discovery:{keyId}");

        using var aes = new AesGcm(_masterKey, 16);
        aes.Decrypt(nonce, cipher, tag, plain, aad);
        return Encoding.UTF8.GetString(plain);
    }

    public string UnprotectOrSelf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (!IsProtected(value))
            return value;

        try
        {
            return Unprotect(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao descriptografar segredo protegido.");
            throw;
        }
    }

    private static byte[]? ResolveMasterKey(string? configuredBase64)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredBase64)
            ? Environment.GetEnvironmentVariable("DISCOVERY_ENCRYPTION_KEY_BASE64")
            : configuredBase64;

        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(candidate.Trim());
            return bytes.Length == 32 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Combine(byte[] nonce, byte[] tag, byte[] cipher)
    {
        var output = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, output, nonce.Length + tag.Length, cipher.Length);
        return output;
    }
}

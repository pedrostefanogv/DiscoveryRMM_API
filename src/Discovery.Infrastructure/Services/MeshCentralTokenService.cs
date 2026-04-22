using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services;

public class MeshCentralTokenService : IMeshCentralTokenService
{
    private static readonly Regex ValidUsername = new("^[a-zA-Z0-9._@-]{1,64}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MeshCentralOptions _options;

    public MeshCentralTokenService(IOptions<MeshCentralOptions> options)
    {
        _options = options.Value;
    }

    public string GenerateControlAuthToken(string username)
        => GenerateToken(username, "MeshCentral technical username is invalid.");

    public string GenerateLoginToken(string username)
        => GenerateToken(username, "MeshCentral username is invalid.");

    private string GenerateToken(string username, string invalidUsernameMessage)
    {
        var normalizedUsername = NormalizeUsername(username, invalidUsernameMessage);

        if (string.IsNullOrWhiteSpace(_options.LoginKeyHex))
            throw new InvalidOperationException("MeshCentral LoginKeyHex is not configured.");

        var loginKeyBytes = ParseHex(_options.LoginKeyHex);
        if (loginKeyBytes.Length < 32)
            throw new InvalidOperationException("MeshCentral LoginKeyHex is invalid.");

        var payload = new MeshCentralTokenPayload
        {
            UserId = $"user/{_options.DomainId}/{normalizedUsername}",
            DomainId = _options.DomainId,
            Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var payloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        var iv = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[payloadBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(loginKeyBytes.AsSpan(0, 32), 16);
        aes.Encrypt(iv, payloadBytes, cipher, tag);

        var packed = new byte[iv.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(iv, 0, packed, 0, iv.Length);
        Buffer.BlockCopy(tag, 0, packed, iv.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, packed, iv.Length + tag.Length, cipher.Length);

        return Convert.ToBase64String(packed)
            .Replace('+', '@')
            .Replace('/', '$');
    }

    private static string NormalizeUsername(string username, string invalidUsernameMessage)
    {
        var normalized = username.Trim().ToLowerInvariant();
        if (!ValidUsername.IsMatch(normalized))
            throw new InvalidOperationException(invalidUsernameMessage);

        return normalized;
    }

    private static byte[] ParseHex(string hex)
    {
        var normalized = hex.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();

        if ((normalized.Length % 2) != 0)
            throw new InvalidOperationException("MeshCentral LoginKeyHex has invalid length.");

        return Convert.FromHexString(normalized);
    }

    private sealed class MeshCentralTokenPayload
    {
        [JsonPropertyName("userid")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("domainid")]
        public string DomainId { get; set; } = string.Empty;

        [JsonPropertyName("time")]
        public long Time { get; set; }
    }
}
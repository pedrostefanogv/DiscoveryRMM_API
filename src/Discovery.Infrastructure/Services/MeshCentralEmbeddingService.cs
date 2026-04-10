using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services;

public class MeshCentralEmbeddingService : IMeshCentralEmbeddingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex ValidUsername = new("^[a-zA-Z0-9._@-]{1,64}$", RegexOptions.Compiled);

    private readonly MeshCentralOptions _options;

    public MeshCentralEmbeddingService(IOptions<MeshCentralOptions> options)
    {
        _options = options.Value;
    }

    public Task<MeshCentralEmbedUrlResult> GenerateAgentEmbedUrlAsync(
        Agent agent,
        Guid clientId,
        int viewMode,
        int? hideMask,
        string? meshNodeId,
        string? gotoDeviceName,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("MeshCentral integration is disabled.");

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("MeshCentral BaseUrl is not configured.");

        if (string.IsNullOrWhiteSpace(_options.LoginKeyHex))
            throw new InvalidOperationException("MeshCentral LoginKeyHex is not configured.");

        if (Array.IndexOf(_options.AllowedViewModes, viewMode) < 0)
            throw new InvalidOperationException($"ViewMode {viewMode} is not allowed.");

        var loginKeyBytes = ParseHex(_options.LoginKeyHex);
        if (loginKeyBytes.Length < 32)
            throw new InvalidOperationException("MeshCentral LoginKeyHex is invalid.");

        var authToken = GenerateAuthToken(loginKeyBytes, _options.DomainId, "admin");
        var baseUri = NormalizeBaseUri(_options.BaseUrl);

        var query = new Dictionary<string, string>
        {
            ["auth"] = authToken,
            ["viewmode"] = viewMode.ToString(),
            ["hide"] = (hideMask ?? _options.DefaultHideMask).ToString(),
            ["discoveryClientId"] = clientId.ToString("D"),
            ["discoverySiteId"] = agent.SiteId.ToString("D"),
            ["discoveryAgentId"] = agent.Id.ToString("D")
        };

        if (!string.IsNullOrWhiteSpace(meshNodeId))
        {
            query["gotonode"] = meshNodeId;
        }
        else
        {
            var targetDeviceName = string.IsNullOrWhiteSpace(gotoDeviceName)
                ? (agent.DisplayName ?? agent.Hostname)
                : gotoDeviceName;

            if (!string.IsNullOrWhiteSpace(targetDeviceName))
                query["gotodevicename"] = targetDeviceName;
        }

        var url = BuildUrl(baseUri, query);
        var expiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.SuggestedSessionMinutes));

        return Task.FromResult(new MeshCentralEmbedUrlResult
        {
            Url = url,
            ExpiresAtUtc = expiresAt,
            ViewMode = viewMode,
            HideMask = hideMask ?? _options.DefaultHideMask
        });
    }

    public Task<MeshCentralEmbedUrlResult> GenerateUserEmbedUrlAsync(
        string meshUsername,
        Guid clientId,
        Guid siteId,
        Guid? agentId,
        int viewMode,
        int? hideMask,
        string? meshNodeId,
        string? gotoDeviceName,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("MeshCentral integration is disabled.");

        if (string.IsNullOrWhiteSpace(meshUsername) || !ValidUsername.IsMatch(meshUsername))
            throw new InvalidOperationException("Mesh username is invalid.");

        if (Array.IndexOf(_options.AllowedViewModes, viewMode) < 0)
            throw new InvalidOperationException($"ViewMode {viewMode} is not allowed.");

        var loginKeyBytes = ParseHex(_options.LoginKeyHex);
        if (loginKeyBytes.Length < 32)
            throw new InvalidOperationException("MeshCentral LoginKeyHex is invalid.");

        var authToken = GenerateAuthToken(loginKeyBytes, _options.DomainId, meshUsername);
        var baseUri = NormalizeBaseUri(_options.BaseUrl);

        var query = new Dictionary<string, string>
        {
            ["auth"] = authToken,
            ["viewmode"] = viewMode.ToString(),
            ["hide"] = (hideMask ?? _options.DefaultHideMask).ToString(),
            ["discoveryClientId"] = clientId.ToString("D"),
            ["discoverySiteId"] = siteId.ToString("D")
        };

        if (agentId.HasValue)
            query["discoveryAgentId"] = agentId.Value.ToString("D");

        if (!string.IsNullOrWhiteSpace(meshNodeId))
        {
            query["gotonode"] = meshNodeId;
        }
        else if (!string.IsNullOrWhiteSpace(gotoDeviceName))
        {
            query["gotodevicename"] = gotoDeviceName;
        }

        var url = BuildUrl(baseUri, query);
        var expiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.SuggestedSessionMinutes));

        return Task.FromResult(new MeshCentralEmbedUrlResult
        {
            Url = url,
            ExpiresAtUtc = expiresAt,
            ViewMode = viewMode,
            HideMask = hideMask ?? _options.DefaultHideMask
        });
    }

    private static string BuildUrl(string baseUrl, Dictionary<string, string> query)
    {
        var sb = new StringBuilder(baseUrl);
        sb.Append(baseUrl.Contains('?') ? '&' : '?');

        var first = true;
        foreach (var kv in query)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
        }

        return sb.ToString();
    }

    private static string NormalizeBaseUri(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (!trimmed.EndsWith('/')) trimmed += '/';
        return trimmed;
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

    private static string GenerateAuthToken(byte[] loginKey, string domainId, string username)
    {
        var payload = new MeshCentralAuthPayload
        {
            UserId = $"user/{domainId}/{username}",
            DomainId = domainId,
            Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var payloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        var iv = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[payloadBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(loginKey.AsSpan(0, 32), 16);
        aes.Encrypt(iv, payloadBytes, cipher, tag);

        var packed = new byte[iv.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(iv, 0, packed, 0, iv.Length);
        Buffer.BlockCopy(tag, 0, packed, iv.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, packed, iv.Length + tag.Length, cipher.Length);

        return Convert.ToBase64String(packed)
            .Replace('+', '@')
            .Replace('/', '$');
    }

    private sealed class MeshCentralAuthPayload
    {
        [JsonPropertyName("userid")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("domainid")]
        public string DomainId { get; set; } = string.Empty;

        [JsonPropertyName("time")]
        public long Time { get; set; }
    }
}

using System.Text.RegularExpressions;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services;

public class MeshCentralConfigService : IMeshCentralConfigService
{
    private static readonly Regex ValidUsername = new("^[a-zA-Z0-9._@-]{1,64}$", RegexOptions.Compiled);

    private readonly MeshCentralOptions _options;

    public MeshCentralConfigService(IOptions<MeshCentralOptions> options)
    {
        _options = options.Value;
    }

    public string GetPublicBaseUrl()
    {
        var raw = string.IsNullOrWhiteSpace(_options.PublicBaseUrl)
            ? _options.BaseUrl
            : _options.PublicBaseUrl;

        return NormalizeAbsoluteUrl(raw, "MeshCentral public BaseUrl is not configured.");
    }

    public string GetAdministrativeBaseUrl()
    {
        var raw = string.IsNullOrWhiteSpace(_options.InternalBaseUrl)
            ? _options.BaseUrl
            : _options.InternalBaseUrl;

        return NormalizeAbsoluteUrl(raw, "MeshCentral administrative BaseUrl is not configured.");
    }

    public string GetTechnicalUsername()
    {
        var normalized = string.IsNullOrWhiteSpace(_options.TechnicalUsername)
            ? "admin"
            : _options.TechnicalUsername.Trim().ToLowerInvariant();

        if (!ValidUsername.IsMatch(normalized))
            throw new InvalidOperationException("MeshCentral technical username is invalid.");

        return normalized;
    }

    public Uri BuildControlWebSocketUri(string authToken)
    {
        if (string.IsNullOrWhiteSpace(authToken))
            throw new InvalidOperationException("MeshCentral auth token is required.");

        var baseUri = new Uri(GetAdministrativeBaseUrl(), UriKind.Absolute);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss",
            Path = AppendPathSegment(baseUri.AbsolutePath, "control.ashx")
        };

        var queryParts = new List<string>();
        if (_options.AdministrativeIncludeKeyInQuery)
        {
            if (string.IsNullOrWhiteSpace(_options.LoginKeyHex))
                throw new InvalidOperationException("MeshCentral LoginKeyHex is not configured.");

            queryParts.Add($"key={Uri.EscapeDataString(NormalizeHex(_options.LoginKeyHex))}");
        }

        queryParts.Add($"auth={Uri.EscapeDataString(authToken)}");
        builder.Query = string.Join("&", queryParts);

        return builder.Uri;
    }

    private static string NormalizeAbsoluteUrl(string rawValue, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            throw new InvalidOperationException(errorMessage);

        if (!Uri.TryCreate(rawValue.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException(errorMessage.Replace("configured", "valid"));

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
            builder.Path += "/";

        return builder.Uri.ToString();
    }

    private static string AppendPathSegment(string basePath, string segment)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(basePath) ? "/" : basePath;
        if (!normalizedBase.StartsWith("/", StringComparison.Ordinal))
            normalizedBase = "/" + normalizedBase;

        if (!normalizedBase.EndsWith("/", StringComparison.Ordinal))
            normalizedBase += "/";

        return normalizedBase + segment;
    }

    private static string NormalizeHex(string hex)
    {
        return hex.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();
    }
}
using System.Text;
using Discovery.Core.Configuration;

namespace Discovery.Infrastructure.Services;

internal static class MeshCentralInstallUrlBuilder
{
    public static string BuildDirectInstallUrl(MeshCentralOptions options, string meshId)
    {
        if (string.IsNullOrWhiteSpace(meshId))
            throw new InvalidOperationException("MeshCentral mesh id is required to build install URL.");

        var baseUrl = string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? options.BaseUrl
            : options.PublicBaseUrl;

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("MeshCentral BaseUrl is not configured.");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException("MeshCentral BaseUrl is invalid.");

        var installFlags = Math.Clamp(options.AgentDownloadInstallFlags, 0, 2);
        var architectureId = options.AgentDownloadArchitectureId <= 0 ? 4 : options.AgentDownloadArchitectureId;

        var builder = new UriBuilder(baseUri)
        {
            Path = AppendPathSegment(baseUri.AbsolutePath, "meshagents")
        };

        var pairs = ParseQueryPairs(baseUri.Query);
        RemoveKey(pairs, "id");
        RemoveKey(pairs, "meshid");
        RemoveKey(pairs, "installflags");

        pairs.Add(("id", architectureId.ToString()));
        pairs.Add(("meshid", meshId));
        pairs.Add(("installflags", installFlags.ToString()));

        builder.Query = BuildQueryString(pairs);
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

    private static List<(string Key, string Value)> ParseQueryPairs(string query)
    {
        var pairs = new List<(string Key, string Value)>();
        if (string.IsNullOrWhiteSpace(query))
            return pairs;

        var raw = query.StartsWith('?') ? query[1..] : query;
        if (string.IsNullOrWhiteSpace(raw))
            return pairs;

        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            var key = idx >= 0 ? part[..idx] : part;
            var value = idx >= 0 ? part[(idx + 1)..] : string.Empty;
            pairs.Add((Uri.UnescapeDataString(key), Uri.UnescapeDataString(value)));
        }

        return pairs;
    }

    private static string BuildQueryString(IEnumerable<(string Key, string Value)> pairs)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (sb.Length > 0)
                sb.Append('&');

            sb.Append(Uri.EscapeDataString(key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(value));
        }

        return sb.ToString();
    }

    private static void RemoveKey(List<(string Key, string Value)> pairs, string key)
    {
        for (var i = pairs.Count - 1; i >= 0; i--)
        {
            if (string.Equals(pairs[i].Key, key, StringComparison.OrdinalIgnoreCase))
                pairs.RemoveAt(i);
        }
    }
}

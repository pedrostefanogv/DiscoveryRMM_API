using System.Text.Json;
using Discovery.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public sealed class WingetFeedClient
{
    private const string WingetLatestPackagesUrl = "https://github.com/pedrostefanogv/winget-package-explo/releases/latest/download/packages.json";
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly ILogger<WingetFeedClient> _logger;

    public WingetFeedClient(ILogger<WingetFeedClient> logger)
    {
        _logger = logger;
    }

    public sealed record WingetCatalogSnapshot(DateTime? GeneratedAt, int TotalPackages, List<WingetPackage> Packages);

    public async Task<WingetCatalogSnapshot> FetchLatestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SharedHttpClient.GetAsync(WingetLatestPackagesUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = json.RootElement;
        var generated = TryGetDateTime(root, "generated");
        var count = TryGetInt(root, "count");

        var packages = new List<WingetPackage>();
        if (root.TryGetProperty("packages", out var packagesElement) && packagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in packagesElement.EnumerateArray())
            {
                var packageId = TryGetString(item, "id").Trim();
                if (string.IsNullOrWhiteSpace(packageId))
                    continue;

                var installerUrls = ParseInstallerUrls(item);
                var silentSwitches = ParseSilentSwitches(item);
                var tags = ParseStringArray(item, "tags");

                packages.Add(new WingetPackage
                {
                    PackageId = packageId,
                    Name = TryGetString(item, "name"),
                    Publisher = TryGetString(item, "publisher"),
                    Version = TryGetString(item, "version"),
                    Description = TryGetString(item, "description"),
                    Homepage = TryGetString(item, "homepage"),
                    License = TryGetString(item, "license"),
                    Category = TryGetString(item, "category"),
                    Icon = TryGetString(item, "icon"),
                    InstallCommand = TryGetString(item, "installCommand"),
                    SilentCommand = silentSwitches.Silent,
                    SilentWithProgressCommand = silentSwitches.SilentWithProgress,
                    LastUpdated = TryGetDateTime(item, "lastUpdated"),
                    SourceGeneratedAt = generated,
                    Tags = string.Join(' ', tags),
                    InstallerUrlsJson = JsonSerializer.Serialize(installerUrls)
                });
            }
        }

        _logger.LogInformation("Winget feed fetched: {Count} packages.", packages.Count);

        return new WingetCatalogSnapshot(
            generated,
            count > 0 ? count : packages.Count,
            packages);
    }

    private static string TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };
    }

    private static DateTime? TryGetDateTime(JsonElement element, string propertyName)
    {
        var raw = TryGetString(element, propertyName);
        if (DateTime.TryParse(raw, out var parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

        return null;
    }

    private static int TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            return intValue;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsedFromString))
            return parsedFromString;

        return 0;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value.Trim());
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseInstallerUrls(JsonElement element)
    {
        if (!element.TryGetProperty("installerUrlsByArch", out var obj) || obj.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var url = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(url))
                result[property.Name] = url.Trim();
        }

        return result;
    }

    private sealed record SilentSwitches(string Silent, string SilentWithProgress);

    /// <summary>
    /// Extrai os switches silenciosos de "installerSwitches" (objeto por arquitetura).
    /// Preferência de arquitetura: x64 → x86 → arm64 → arm → neutral.
    /// Fallback dentro da arquitetura escolhida: Silent → SilentWithProgress.
    /// Todas as comparações de chave são case-insensitive.
    /// </summary>
    private static SilentSwitches ParseSilentSwitches(JsonElement item)
    {
        if (!TryGetPropertyIgnoreCase(item, "installerSwitches", out var switchesObj) ||
            switchesObj.ValueKind != JsonValueKind.Object)
        {
            return new SilentSwitches(string.Empty, string.Empty);
        }

        // Indexa as arquiteturas por nome normalizado (case-insensitive).
        // Usa indexer para tolerar chaves duplicadas no feed (ex.: "x64" e "X64").
        var archs = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var arch in switchesObj.EnumerateObject())
            archs[arch.Name.Trim()] = arch.Value;

        foreach (var preferred in new[] { "x64", "x86", "arm64", "arm", "neutral" })
        {
            if (!archs.TryGetValue(preferred, out var archObj) || archObj.ValueKind != JsonValueKind.Object)
                continue;

            var silent = TryGetStringIgnoreCase(archObj, "Silent");
            var silentWithProgress = TryGetStringIgnoreCase(archObj, "SilentWithProgress");

            // Fallback: se a arquitetura preferida não tem Silent, usa SilentWithProgress dela.
            if (!string.IsNullOrWhiteSpace(silent) || !string.IsNullOrWhiteSpace(silentWithProgress))
                return new SilentSwitches(silent, silentWithProgress);
        }

        return new SilentSwitches(string.Empty, string.Empty);
    }

    /// <summary>Busca uma propriedade ignorando diferenças de caixa no nome.</summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Busca uma propriedade string ignorando diferenças de caixa no nome da propriedade.</summary>
    private static string TryGetStringIgnoreCase(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return string.Empty;

        if (value.ValueKind == JsonValueKind.String)
            return value.GetString()?.Trim() ?? string.Empty;

        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            return value.GetRawText();

        return string.Empty;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("discovery-AppStore/1.0");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}

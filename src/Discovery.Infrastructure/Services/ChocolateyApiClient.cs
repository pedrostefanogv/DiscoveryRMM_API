using System.Net;
using System.Xml.Linq;
using Discovery.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Client for querying the Chocolatey Community Repository OData v2 API.
/// Fetches paginated lists of the latest stable packages.
/// </summary>
public class ChocolateyApiClient
{
    private const string BaseUrl = "https://community.chocolatey.org/api/v2";
    private const int PageSize = 500;
    private const int MaxAttemptsPerPage = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PageDelay = TimeSpan.FromSeconds(2);

    // OData XML namespaces
    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace DataNs = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace MetaNs = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly ILogger<ChocolateyApiClient> _logger;

    public ChocolateyApiClient(ILogger<ChocolateyApiClient> logger)
    {
        _logger = logger;
    }

    public sealed record ChocolateyPackagePage(List<ChocolateyPackage> Packages, string? NextPageUrl);

    /// <summary>
    /// Fetches all latest-version packages from the Chocolatey community feed,
    /// paginating through the full catalog. Returns an async enumerable of page batches.
    /// </summary>
    public async IAsyncEnumerable<ChocolateyPackagePage> FetchAllPackagesAsync(
        string? startUrl = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var nextUrl = string.IsNullOrWhiteSpace(startUrl) ? BuildPageUrl() : startUrl;

        while (!string.IsNullOrWhiteSpace(nextUrl) && !cancellationToken.IsCancellationRequested)
        {
            var url = nextUrl;
            _logger.LogDebug("Fetching Chocolatey packages page: {Url}", url);

            List<ChocolateyPackage>? page = null;
            string? resolvedNextUrl = null;
            for (int attempt = 1; attempt <= MaxAttemptsPerPage; attempt++)
            {
                try
                {
                    var result = await FetchPageAsync(url, cancellationToken);
                    page = result.Packages;
                    resolvedNextUrl = result.NextPageUrl;
                    break;
                }
                catch (HttpRequestException ex) when ((int?)ex.StatusCode == 429)
                {
                    _logger.LogWarning("Rate limited by Chocolatey API (attempt {Attempt}). Waiting 60s.", attempt);
                    await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttemptsPerPage)
                {
                    var delay = TimeSpan.FromSeconds(5 * attempt);
                    _logger.LogWarning(ex, "Chocolatey page request timeout (attempt {Attempt}/{MaxAttempts}). Retrying in {Delay}s.", attempt, MaxAttemptsPerPage, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken);
                }
                catch (Exception ex) when (attempt < MaxAttemptsPerPage)
                {
                    _logger.LogWarning(ex, "Error fetching Chocolatey page (attempt {Attempt}). Retrying.", attempt);
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
                }
            }

            if (page is null)
                break;

            if (page.Count == 0)
                break;

            yield return new ChocolateyPackagePage(page, resolvedNextUrl);

            nextUrl = resolvedNextUrl;
            if (!string.IsNullOrWhiteSpace(nextUrl))
            {
                // Respectful delay between pages
                await Task.Delay(PageDelay, cancellationToken);
            }
        }
    }

    private async Task<(List<ChocolateyPackage> Packages, string? NextPageUrl)> FetchPageAsync(string url, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);

        _logger.LogInformation("Chocolatey request URL: {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml, application/xml;q=0.9, */*;q=0.8");

        using var response = await SharedHttpClient.SendAsync(request, cts.Token);

        if ((int)response.StatusCode == 429)
            throw new HttpRequestException("Rate limited", null, response.StatusCode);

        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(cts.Token);
        return ParseFeedXml(xml);
    }

    private (List<ChocolateyPackage> Packages, string? NextPageUrl) ParseFeedXml(string xml)
    {
        var packages = new List<ChocolateyPackage>();

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Chocolatey OData XML response");
            return (packages, null);
        }

        var entries = doc.Root?.Elements(AtomNs + "entry") ?? Enumerable.Empty<XElement>();

        foreach (var entry in entries)
        {
            var props = entry
                .Element(MetaNs + "properties")
                ?? entry.Element(AtomNs + "content")?.Element(MetaNs + "properties");

            if (props is null)
                continue;

            var packageId = GetPropString(props, "Id");
            if (string.IsNullOrWhiteSpace(packageId))
                packageId = entry.Element(AtomNs + "title")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(packageId))
                packageId = ExtractPackageIdFromEntryId(entry.Element(AtomNs + "id")?.Value);
            if (string.IsNullOrWhiteSpace(packageId))
                continue;

            // Prefer d:Title, fall back to Atom <title>
            var name = GetPropString(props, "Title");
            if (string.IsNullOrWhiteSpace(name))
                name = entry.Element(AtomNs + "title")?.Value ?? string.Empty;

            // Prefer d:Description, fall back to d:Summary then Atom <summary>
            var description = GetPropString(props, "Description");
            if (string.IsNullOrWhiteSpace(description))
                description = GetPropString(props, "Summary");
            if (string.IsNullOrWhiteSpace(description))
                description = entry.Element(AtomNs + "summary")?.Value ?? string.Empty;

            // Authors: prefer d:Authors, fall back to Atom <author><name>
            var publisher = GetPropString(props, "Authors");
            if (string.IsNullOrWhiteSpace(publisher))
                publisher = entry.Element(AtomNs + "author")?.Element(AtomNs + "name")?.Value ?? string.Empty;

            var pkg = new ChocolateyPackage
            {
                PackageId = packageId.Trim().ToLowerInvariant(),
                Name = Truncate(name, 500),
                Publisher = Truncate(publisher, 500),
                Version = Truncate(GetPropString(props, "Version"), 100),
                Description = description,
                Homepage = Truncate(GetPropString(props, "ProjectUrl"), 2000),
                LicenseUrl = Truncate(GetPropString(props, "LicenseUrl"), 2000),
                Tags = Truncate(NormalizeTags(GetPropString(props, "Tags")), 2000),
                DownloadCount = GetPropLong(props, "DownloadCount"),
                LastUpdated = GetPropDateTime(props, "LastUpdated") ?? GetPropDateTime(props, "Published")
            };

            packages.Add(pkg);
        }

        var nextPageUrl = doc.Root?
            .Elements(AtomNs + "link")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("rel"), "next", StringComparison.OrdinalIgnoreCase))?
            .Attribute("href")?
            .Value;

        nextPageUrl = NormalizeNextPageUrl(nextPageUrl);

        return (packages, nextPageUrl);
    }

    private static string? NormalizeNextPageUrl(string? nextPageUrl)
    {
        if (string.IsNullOrWhiteSpace(nextPageUrl))
            return null;

        if (nextPageUrl.StartsWith("http://community.chocolatey.org", StringComparison.OrdinalIgnoreCase))
            nextPageUrl = "https://" + nextPageUrl["http://".Length..];

        const string skipTokenKey = "$skiptoken=";
        var skipTokenIndex = nextPageUrl.IndexOf(skipTokenKey, StringComparison.OrdinalIgnoreCase);
        if (skipTokenIndex >= 0)
        {
            var tokenStart = skipTokenIndex + skipTokenKey.Length;
            var tokenEnd = nextPageUrl.IndexOf('&', tokenStart);
            if (tokenEnd < 0)
                tokenEnd = nextPageUrl.Length;

            var rawToken = nextPageUrl[tokenStart..tokenEnd];
            var normalizedToken = Uri.EscapeDataString(Uri.UnescapeDataString(rawToken));

            nextPageUrl = nextPageUrl[..tokenStart] + normalizedToken + nextPageUrl[tokenEnd..];
        }

        return nextPageUrl;
    }

    private static string BuildPageUrl()
    {
         // OData v2 endpoint requires URL-encoded expressions in $filter and $orderby.
         var filter = Uri.EscapeDataString("IsLatestVersion eq true and IsPrerelease eq false");
         var orderBy = Uri.EscapeDataString("Id");

         // Filter: only latest stable versions, order by Id for consistent pagination
         return $"{BaseUrl}/Packages()" +
             $"?$filter={filter}" +
             $"&$top={PageSize}" +
               $"&$skip=0" +
             $"&$orderby={orderBy}";
    }

    private static string GetPropString(XElement props, string name)
    {
        var el = props.Element(DataNs + name);
        if (el is null || el.Attribute(MetaNs + "null")?.Value == "true")
            return string.Empty;
        return el.Value ?? string.Empty;
    }

    private static long GetPropLong(XElement props, string name)
    {
        var val = GetPropString(props, name);
        return long.TryParse(val, out var n) ? n : 0;
    }

    private static DateTime? GetPropDateTime(XElement props, string name)
    {
        var val = GetPropString(props, name);
        if (string.IsNullOrWhiteSpace(val))
            return null;
        return DateTime.TryParse(val, out var dt)
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : null;
    }

    private static string NormalizeTags(string tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
            return string.Empty;
        // Tags arrive as space-separated; normalize whitespace, lowercase
        return string.Join(" ", tags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant()));
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string ExtractPackageIdFromEntryId(string? entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
            return string.Empty;

        const string marker = "Packages(Id='";
        var start = entryId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;

        start += marker.Length;
        var end = entryId.IndexOf("'", start, StringComparison.Ordinal);
        if (end <= start)
            return string.Empty;

        return entryId[start..end];
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "discovery-RMM/1.0 (ChocolateyIntegration)");
        return client;
    }
}

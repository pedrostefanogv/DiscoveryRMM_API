using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Busca manifestos de instalador no repositório `microsoft/winget-pkgs`
/// (WU-21/D24). Usado pelo job de ingestão: descobre a versão mais recente de
/// um app presente no inventário e baixa o arquivo `.installer.yaml`.
///
/// Regras:
///  • o caminho é determinístico (`manifests/&lt;1ª letra&gt;/&lt;segmentos do id&gt;/&lt;versão&gt;/&lt;id&gt;.installer.yaml`);
///  • a "versão mais recente" vem do diretório do pacote na API de conteúdo do
///    GitHub, comparando versões numericamente (156.0.10 &gt; 156.0.9, ao contrário da ordem textual);
///  • falha de rede/404 não é exceção do job: devolve null e o chamador decide.
/// </summary>
public sealed class WingetManifestSource
{
    private const string RawBase = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/";
    private const string ApiBase = "https://api.github.com/repos/microsoft/winget-pkgs/contents/";
    private const string RepositoryPrefix = "manifests/";
    private const string UserAgent = "DiscoveryRMM-PatchCatalog/1.0";

    private static readonly Regex VersionLikePattern = new(@"^\d", RegexOptions.Compiled);

    private readonly HttpClient _http;

    /// <summary>
    /// <paramref name="httpClient"/> permite injetar um handler fake nos testes;
    /// quando nulo, cria um client com timeout e User-Agent (o GitHub exige UA em
    /// todas as requisições).
    /// </summary>
    public WingetManifestSource(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>Caminho do manifesto dentro do repositório.</summary>
    public static string BuildManifestPath(string packageId, string version)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("packageId e version sao obrigatorios");

        var id = packageId.Trim();
        var segments = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new ArgumentException("packageId invalido");

        var first = char.ToLowerInvariant(segments[0][0]);
        var parts = new List<string> { RepositoryPrefix + first };
        parts.AddRange(segments);
        parts.Add(version.Trim());
        parts.Add(id + ".installer.yaml");
        return string.Join('/', parts);
    }

    /// <summary>Escolhe a versão mais recente entre os diretórios do pacote.</summary>
    public static string? SelectLatestVersion(IEnumerable<string>? names)
    {
        if (names is null)
            return null;

        var candidates = names
            .Where(name => !string.IsNullOrWhiteSpace(name) && VersionLikePattern.IsMatch(name.Trim()))
            .Select(name => name.Trim())
            .ToList();
        if (candidates.Count == 0)
            return null;

        candidates.Sort(CompareVersions);
        return candidates[^1];
    }

    /// <summary>Compara versões numericamente por segmentos (semântica winget).</summary>
    public static int CompareVersions(string left, string right)
    {
        var leftParts = SplitVersion(left);
        var rightParts = SplitVersion(right);
        var length = Math.Max(leftParts.Count, rightParts.Count);
        for (var i = 0; i < length; i++)
        {
            var a = i < leftParts.Count ? leftParts[i] : "0";
            var b = i < rightParts.Count ? rightParts[i] : "0";

            var aNumeric = long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var an);
            var bNumeric = long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bn);
            if (aNumeric && bNumeric)
            {
                if (an != bn)
                    return an.CompareTo(bn);
                continue;
            }

            var comparison = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }

    /// <summary>Versão mais recente publicada para o app (null se não encontrado).</summary>
    public async Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct = default)
    {
        var segments = packageId.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var url = ApiBase + RepositoryPrefix + char.ToLowerInvariant(segments[0][0]) + "/" +
                  string.Join('/', segments);
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var names = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String)
                {
                    names.Add(nameElement.GetString() ?? string.Empty);
                }
            }
            return SelectLatestVersion(names);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Conteúdo do manifesto de instalador (null se indisponível).</summary>
    public async Task<string?> GetInstallerManifestAsync(
        string packageId,
        string version,
        CancellationToken ct = default)
    {
        var url = RawBase + BuildManifestPath(packageId, version);
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private static List<string> SplitVersion(string version) =>
        version.Trim()
            .Split(new[] { '.', '-', '_', '+' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
}

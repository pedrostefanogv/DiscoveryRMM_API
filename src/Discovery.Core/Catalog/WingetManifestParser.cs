using System.Globalization;
using System.Text.RegularExpressions;

namespace Discovery.Core.Catalog;

/// <summary>
/// Parser do subconjunto de YAML usado pelos manifestos do `winget-pkgs`
/// (WU-21/D24). Não é um parser YAML genérico: lê apenas o que o catálogo
/// precisa — PackageIdentifier/Version/Name/Publisher, ReleaseDate,
/// AppsAndFeaturesEntries (ProductCode) e Installers (URL, SHA-256, tipo,
/// switches silenciosos).
///
/// Por que um parser próprio: adicionar YamlDotNet só para isto aumenta a
/// superfície de dependência; os manifestos são gerados por ferramenta
/// (estrutura estável). O parser FALHA FECHADO: campo ausente, URL não-https ou
/// hash inválido → erro, nunca entrada parcial.
/// </summary>
public static class WingetManifestParser
{
    public sealed record WingetInstaller(
        string Architecture,
        string InstallerUrl,
        string Sha256,
        string? InstallerType,
        string? SilentArgs,
        string? ProductCode);

    public sealed record WingetManifest(
        string PackageIdentifier,
        string PackageVersion,
        string? PackageName,
        string? Publisher,
        DateTime? ReleaseDate,
        WingetInstaller Installer);

    private static readonly Regex Sha256Pattern = new("^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    /// <summary>Parseia o manifesto; devolve erro legível quando insuficiente.</summary>
    public static (WingetManifest? Manifest, string? Error) Parse(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return (null, "manifesto vazio");

        var lines = yaml
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => StripComment(line).TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var top = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var appsAndFeatures = new List<Dictionary<string, string>>();
        var installers = new List<Dictionary<string, string>>();

        var section = Section.None;
        Dictionary<string, string>? currentItem = null;
        var nestedKey = string.Empty;

        foreach (var raw in lines)
        {
            var indent = raw.Length - raw.TrimStart(' ').Length;
            var trimmed = raw.TrimStart(' ');

            if (indent == 0 && !trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var (key, value) = SplitKeyValue(trimmed);
                if (value is null)
                {
                    section = key switch
                    {
                        "Installers" => Section.Installers,
                        "AppsAndFeaturesEntries" => Section.AppsAndFeatures,
                        _ => Section.None
                    };
                    currentItem = null;
                    nestedKey = string.Empty;
                    continue;
                }

                section = Section.None;
                currentItem = null;
                top[key] = value;
                continue;
            }

            if (section is not (Section.Installers or Section.AppsAndFeatures))
                continue;

            var target = section == Section.Installers ? installers : appsAndFeatures;

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                currentItem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                target.Add(currentItem);
                nestedKey = string.Empty;
                var (key, value) = SplitKeyValue(trimmed[2..]);
                if (value is not null)
                    currentItem[key] = value;
                continue;
            }

            if (currentItem is null)
                continue;

            var (itemKey, itemValue) = SplitKeyValue(trimmed);
            if (itemValue is null)
            {
                nestedKey = itemKey;
                continue;
            }

            if (!string.IsNullOrEmpty(nestedKey))
            {
                currentItem[nestedKey + "." + itemKey] = itemValue;
                continue;
            }

            currentItem[itemKey] = itemValue;
        }

        var packageId = Get(top, "PackageIdentifier");
        var version = Get(top, "PackageVersion");
        if (string.IsNullOrWhiteSpace(packageId))
            return (null, "PackageIdentifier ausente");
        if (string.IsNullOrWhiteSpace(version))
            return (null, "PackageVersion ausente");

        if (installers.Count == 0)
            return (null, "manifesto sem Installers (use o .installer.yaml, não o .yaml de versão)");

        var chosen = installers.FirstOrDefault(item =>
                         string.Equals(Get(item, "Architecture"), "x64", StringComparison.OrdinalIgnoreCase))
                     ?? installers.FirstOrDefault(item =>
                         string.Equals(Get(item, "Architecture"), "arm64", StringComparison.OrdinalIgnoreCase))
                     ?? installers[0];

        var url = Get(chosen, "InstallerUrl");
        var sha = Get(chosen, "InstallerSha256");
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (null, "InstallerUrl ausente ou nao-https");
        if (string.IsNullOrWhiteSpace(sha) || !Sha256Pattern.IsMatch(sha.Trim()))
            return (null, "InstallerSha256 ausente ou invalido");

        var installerType = Get(chosen, "InstallerType") ?? Get(top, "InstallerType");
        var silent = Get(chosen, "InstallerSwitches.Silent") ?? Get(chosen, "InstallerSwitches.SilentWithProgress");

        var productCode = Get(chosen, "ProductCode") ??
                          appsAndFeatures.Select(item => Get(item, "ProductCode"))
                              .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        DateTime? releaseDate = null;
        var rawDate = Get(top, "ReleaseDate");
        if (!string.IsNullOrWhiteSpace(rawDate) &&
            DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            releaseDate = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        var manifest = new WingetManifest(
            packageId.Trim(),
            version.Trim(),
            Get(top, "PackageName"),
            Get(top, "Publisher"),
            releaseDate,
            new WingetInstaller(
                Get(chosen, "Architecture") ?? "unknown",
                url.Trim(),
                sha.Trim().ToLowerInvariant(),
                installerType,
                silent,
                productCode));

        return (manifest, null);
    }

    private enum Section
    {
        None,
        Installers,
        AppsAndFeatures
    }

    private static string StripComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '#')
                continue;
            if (i == 0 || line[i - 1] == ' ')
                return line[..i];
        }
        return line;
    }

    private static (string Key, string? Value) SplitKeyValue(string text)
    {
        var separator = text.IndexOf(':');
        if (separator <= 0)
            return (text.Trim(), null);

        var key = text[..separator].Trim();
        var value = text[(separator + 1)..].Trim();
        return value.Length == 0 ? (key, null) : (key, Unquote(value));
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }

    private static string? Get(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) ? value : null;
}

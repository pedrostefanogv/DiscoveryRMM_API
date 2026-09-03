using Discovery.Core.Entities;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Parser dos manifests YAML do microsoft/winget-pkgs (Installer.yaml + DefaultLocale.yaml)
/// para AppPackage. Tolerante a manifests malformados: retorna null e o chamador pula o pacote.
/// </summary>
public sealed class WingetManifestParser
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(PascalCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parseia o diretório de uma versão de pacote (padrão winget-pkgs:
    /// &lt;PackageId&gt;.installer.yaml, &lt;PackageId&gt;.locale.en-US.yaml; aceita também
    /// os nomes genéricos Installer.yaml/DefaultLocale.yaml).
    /// Retorna null se o manifest for inutilizável (sem installers válidos, id divergente, etc.).
    /// </summary>
    public AppPackage? Parse(string packageIdentifier, string version, string versionDirectory, DateTime? sourceGeneratedAt = null)
    {
        try
        {
            var installer = FindYaml(versionDirectory, ["Installer.yaml", $"{packageIdentifier}.installer.yaml"])
                is { } installerPath ? TryReadYaml<InstallerManifest>(installerPath) : null;
            var locale = FindYaml(versionDirectory,
                    [$"{packageIdentifier}.locale.en-US.yaml", $"{packageIdentifier}.locale.yaml", "DefaultLocale.yaml"])
                is { } localePath ? TryReadYaml<DefaultLocaleManifest>(localePath) : null;

            if (installer is null)
                return null;

            // Cross-check de consistência com o path (manifests malformados existem no repo).
            if (!string.Equals(installer.PackageIdentifier, packageIdentifier, StringComparison.OrdinalIgnoreCase))
                return null;

            if (installer.Installers is null || installer.Installers.Count == 0)
                return null;

            var installersByArch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var shaByArch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var installerTypesByArch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in installer.Installers)
            {
                if (string.IsNullOrWhiteSpace(entry.Architecture) || string.IsNullOrWhiteSpace(entry.InstallerUrl))
                    continue;

                var arch = entry.Architecture.Trim().ToLowerInvariant();
                installersByArch[arch] = entry.InstallerUrl.Trim();
                if (!string.IsNullOrWhiteSpace(entry.InstallerSha256))
                    shaByArch[arch] = entry.InstallerSha256!.Trim();
                // InstallerType (ex.: "wix", "burn", "exe", "msi", "nullsoft",
                // "inno", "zip", "portable") permite ao agent decidir como
                // executar o instalador baixado sem adivinhar pela extensão.
                if (!string.IsNullOrWhiteSpace(entry.InstallerType))
                    installerTypesByArch[arch] = entry.InstallerType!.Trim().ToLowerInvariant();
            }

            if (installersByArch.Count == 0)
                return null;

            var switches = ResolveSilentSwitches(installer.Installers);
            var tags = locale?.Tags ?? [];

            var metadata = new
            {
                license = locale?.License,
                category = (string?)null,
                tags,
                installerUrlsByArch = installersByArch,
                installerSha256ByArch = shaByArch,
                installerTypesByArch = installerTypesByArch,
                silent = switches.Silent,
                silentWithProgress = switches.SilentWithProgress,
                installLocation = switches.InstallLocation
            };

            return new AppPackage
            {
                PackageId = packageIdentifier,
                Version = version,
                Name = FirstNonEmpty(locale?.PackageName, packageIdentifier)!,
                Publisher = locale?.Publisher ?? string.Empty,
                Description = locale?.ShortDescription ?? string.Empty,
                SiteUrl = FirstNonEmpty(locale?.PackageUrl),
                IconUrl = null,
                InstallCommand = string.Empty,
                MetadataJson = JsonSerializer.Serialize(metadata),
                SourceGeneratedAt = sourceGeneratedAt,
                LastUpdated = null
            };
        }
        catch (Exception)
        {
            // Manifest malformado: pula o pacote (nunca aborta o batch).
            return null;
        }
    }

    private static (string? Silent, string? SilentWithProgress, string? InstallLocation) ResolveSilentSwitches(
        List<InstallerEntry> installers)
    {
        // Preferência de arquitetura igual ao WingetFeedClient: x64 → x86 → arm64 → arm → neutral.
        string?[] archOrder = ["x64", "x86", "arm64", "arm", "neutral"];

        InstallerSwitches? chosen = null;
        foreach (var arch in archOrder)
        {
            chosen = installers
                .FirstOrDefault(i => string.Equals(i.Architecture?.Trim(), arch, StringComparison.OrdinalIgnoreCase))
                ?.InstallerSwitches;
            if (chosen is not null)
                break;
        }

        chosen ??= installers.Select(i => i.InstallerSwitches).FirstOrDefault(s => s is not null);

        // Fallback mútuo: Silent → SilentWithProgress e vice-versa.
        var silent = FirstNonEmpty(chosen?.Silent, chosen?.SilentWithProgress);
        var silentWithProgress = FirstNonEmpty(chosen?.SilentWithProgress, chosen?.Silent);
        return (silent, silentWithProgress, chosen?.InstallLocation);
    }

    private static T? TryReadYaml<T>(string path) where T : class
    {
        try
        {
            using var reader = File.OpenText(path);
            return Yaml.Deserialize<T>(reader);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindYaml(string dir, string[] candidates)
    {
        foreach (var name in candidates)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
                return path;
        }

        // Locale pode ser outro idioma (ex. .locale.pt-BR.yaml) — pega qualquer *.locale.*.yaml.
        if (candidates.Any(c => c.Contains(".locale.")))
        {
            var anyLocale = Directory.EnumerateFiles(dir, "*.locale.*.yaml").FirstOrDefault();
            if (anyLocale is not null)
                return anyLocale;
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    // ── Modelos YAML (deserialização tolerante: campos ausentes = null) ──

    private sealed class InstallerManifest
    {
        public string? PackageIdentifier { get; set; }
        public string? PackageVersion { get; set; }
        public List<InstallerEntry>? Installers { get; set; }
    }

    private sealed class InstallerEntry
    {
        public string? Architecture { get; set; }
        public string? InstallerUrl { get; set; }
        public string? InstallerSha256 { get; set; }
        public string? InstallerType { get; set; }
        public InstallerSwitches? InstallerSwitches { get; set; }
    }

    private sealed class InstallerSwitches
    {
        public string? Silent { get; set; }
        public string? SilentWithProgress { get; set; }
        public string? InstallLocation { get; set; }
    }

    private sealed class DefaultLocaleManifest
    {
        public string? PackageIdentifier { get; set; }
        public string? PackageName { get; set; }
        public string? Publisher { get; set; }
        public string? License { get; set; }
        public string? ShortDescription { get; set; }
        public string? PackageUrl { get; set; }
        public List<string>? Tags { get; set; }
    }
}

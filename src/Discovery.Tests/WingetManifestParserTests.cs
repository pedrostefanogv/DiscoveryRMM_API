using Discovery.Core.Entities;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes do parser de manifests YAML do microsoft/winget-pkgs.
/// </summary>
public class WingetManifestParserTests
{
    private static readonly WingetManifestParser Parser = new();

    private static string CreateVersionDir(
        string? installerYaml,
        string? defaultLocaleYaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "winget-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        if (installerYaml is not null)
            File.WriteAllText(Path.Combine(dir, "Installer.yaml"), installerYaml);

        if (defaultLocaleYaml is not null)
            File.WriteAllText(Path.Combine(dir, "DefaultLocale.yaml"), defaultLocaleYaml);

        return dir;
    }

    [Test]
    public void Parse_CompleteManifest_MapsAllFields()
    {
        var dir = CreateVersionDir(
            """
            PackageIdentifier: Foxit.FoxitReader
            PackageVersion: 2026.1.3.36551
            Installers:
              - Architecture: x64
                InstallerUrl: https://example.com/foxit-x64.exe
                InstallerSha256: aabbcc
                InstallerType: exe
                InstallerSwitches:
                  Silent: /SILENT
                  SilentWithProgress: /SILENT /NORD
                  InstallLocation: <INSTALLDIR>
              - Architecture: x86
                InstallerUrl: https://example.com/foxit-x86.exe
                InstallerSha256: ddeeff
            """,
            """
            PackageIdentifier: Foxit.FoxitReader
            PackageName: Foxit PDF Editor
            Publisher: Foxit Software
            License: Proprietary
            ShortDescription: PDF editor
            PackageUrl: https://www.foxit.com
            Tags:
              - pdf
              - editor
            """);

        var result = Parser.Parse("Foxit.FoxitReader", "2026.1.3.36551", dir);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.PackageId, Is.EqualTo("Foxit.FoxitReader"));
            Assert.That(result.Version, Is.EqualTo("2026.1.3.36551"));
            Assert.That(result.Name, Is.EqualTo("Foxit PDF Editor"));
            Assert.That(result.Publisher, Is.EqualTo("Foxit Software"));
            Assert.That(result.SiteUrl, Is.EqualTo("https://www.foxit.com"));

            using var meta = System.Text.Json.JsonDocument.Parse(result.MetadataJson!);
            Assert.That(meta.RootElement.GetProperty("silent").GetString(), Is.EqualTo("/SILENT"));
            Assert.That(meta.RootElement.GetProperty("silentWithProgress").GetString(), Is.EqualTo("/SILENT /NORD"));
            Assert.That(meta.RootElement.GetProperty("installerUrlsByArch").GetProperty("x64").GetString(),
                Is.EqualTo("https://example.com/foxit-x64.exe"));
            Assert.That(meta.RootElement.GetProperty("installerUrlsByArch").GetProperty("x86").GetString(),
                Is.EqualTo("https://example.com/foxit-x86.exe"));
            Assert.That(meta.RootElement.GetProperty("installerSha256ByArch").GetProperty("x64").GetString(), Is.EqualTo("aabbcc"));
            Assert.That(meta.RootElement.GetProperty("tags").GetArrayLength(), Is.EqualTo(2));
        });
    }

    [Test]
    public void Parse_PrefersX64SilentSwitches()
    {
        var dir = CreateVersionDir(
            """
            PackageIdentifier: Some.App
            Installers:
              - Architecture: x86
                InstallerUrl: https://example.com/x86.exe
                InstallerSwitches:
                  Silent: /X86SILENT
              - Architecture: x64
                InstallerUrl: https://example.com/x64.exe
                InstallerSwitches:
                  Silent: /X64SILENT
            """,
            null);

        var result = Parser.Parse("Some.App", "1.0", dir);

        Assert.That(result, Is.Not.Null);
        using var meta = System.Text.Json.JsonDocument.Parse(result!.MetadataJson!);
        Assert.That(meta.RootElement.GetProperty("silent").GetString(), Is.EqualTo("/X64SILENT"));
    }

    [Test]
    public void Parse_MissingInstallerYaml_ReturnsNull()
    {
        var dir = CreateVersionDir(null, "PackageIdentifier: Some.App\nPackageName: App");

        var result = Parser.Parse("Some.App", "1.0", dir);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Parse_IdentifierMismatch_ReturnsNull()
    {
        var dir = CreateVersionDir(
            "PackageIdentifier: Other.App\nInstallers:\n  - Architecture: x64\n    InstallerUrl: https://example.com/a.exe",
            null);

        var result = Parser.Parse("Some.App", "1.0", dir);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Parse_NoInstallers_ReturnsNull()
    {
        var dir = CreateVersionDir(
            "PackageIdentifier: Some.App\nInstallers: []",
            null);

        var result = Parser.Parse("Some.App", "1.0", dir);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Parse_MalformedYaml_ReturnsNullWithoutThrowing()
    {
        var dir = CreateVersionDir(":::::: not yaml at all [[[[", null);

        var result = Parser.Parse("Some.App", "1.0", dir);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Parse_SilentFallback_SilentWithProgressUsedWhenSilentMissing()
    {
        var dir = CreateVersionDir(
            """
            PackageIdentifier: Some.App
            Installers:
              - Architecture: x64
                InstallerUrl: https://example.com/a.exe
                InstallerSwitches:
                  SilentWithProgress: /PASSIVE
            """,
            null);

        var result = Parser.Parse("Some.App", "1.0", dir);

        Assert.That(result, Is.Not.Null);
        using var meta = System.Text.Json.JsonDocument.Parse(result!.MetadataJson!);
        Assert.That(meta.RootElement.GetProperty("silent").GetString(), Is.EqualTo("/PASSIVE"));
        Assert.That(meta.RootElement.GetProperty("silentWithProgress").GetString(), Is.EqualTo("/PASSIVE"));
    }
}

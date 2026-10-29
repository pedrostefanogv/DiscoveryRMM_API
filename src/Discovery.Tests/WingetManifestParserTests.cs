using Discovery.Core.Catalog;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class WingetManifestParserTests
{
    private static string LoadRealManifest()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "Google.Chrome.installer.yaml");
        return File.ReadAllText(path);
    }

    [Test]
    public void ParsesRealGoogleChromeManifest()
    {
        var (manifest, error) = WingetManifestParser.Parse(LoadRealManifest());

        Assert.That(error, Is.Null);
        Assert.That(manifest, Is.Not.Null);
        Assert.That(manifest!.PackageIdentifier, Is.EqualTo("Google.Chrome"));
        Assert.That(manifest.PackageVersion, Is.EqualTo("156.0.8078.48"));
        Assert.That(manifest.Installer.InstallerUrl, Does.StartWith("https://dl.google.com/"));
        Assert.That(manifest.Installer.Sha256, Has.Length.EqualTo(64));
        Assert.That(manifest.Installer.Architecture, Is.EqualTo("x64"), "x64 é preferido");
        Assert.That(manifest.Installer.InstallerType, Is.EqualTo("wix"));
        Assert.That(manifest.Installer.ProductCode, Is.EqualTo("{FC681062-00FC-3E42-98D1-2B1A9AF642A3}"));
        Assert.That(manifest.ReleaseDate, Is.Not.Null);
    }

    [Test]
    public void ParsesSilentSwitchesFromNestedBlock()
    {
        const string yaml = """
        PackageIdentifier: Vendor.App
        PackageVersion: 1.2.3
        InstallerType: exe
        Installers:
        - Architecture: x64
          InstallerUrl: https://example.com/app.exe
          InstallerSha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
          InstallerSwitches:
            Silent: /S
            SilentWithProgress: /S
        """;

        var (manifest, error) = WingetManifestParser.Parse(yaml);

        Assert.That(error, Is.Null);
        Assert.That(manifest!.Installer.SilentArgs, Is.EqualTo("/S"));
        Assert.That(manifest.Installer.InstallerType, Is.EqualTo("exe"));
    }

    [Test]
    public void RejectsManifestWithoutInstallers()
    {
        const string yaml = """
        PackageIdentifier: Vendor.App
        PackageVersion: 1.2.3
        ManifestType: version
        """;

        var (manifest, error) = WingetManifestParser.Parse(yaml);

        Assert.That(manifest, Is.Null);
        Assert.That(error, Does.Contain("Installers"));
    }

    [TestCase("http://example.com/app.exe")]
    [TestCase("")]
    public void RejectsNonHttpsUrl(string url)
    {
        var yaml = $"""
        PackageIdentifier: Vendor.App
        PackageVersion: 1.2.3
        Installers:
        - Architecture: x64
          InstallerUrl: {url}
          InstallerSha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
        """;

        var (manifest, error) = WingetManifestParser.Parse(yaml);

        Assert.That(manifest, Is.Null);
        Assert.That(error, Does.Contain("InstallerUrl"));
    }

    [Test]
    public void RejectsInvalidSha256()
    {
        const string yaml = """
        PackageIdentifier: Vendor.App
        PackageVersion: 1.2.3
        Installers:
        - Architecture: x64
          InstallerUrl: https://example.com/app.exe
          InstallerSha256: nao-e-hash
        """;

        var (manifest, error) = WingetManifestParser.Parse(yaml);

        Assert.That(manifest, Is.Null);
        Assert.That(error, Does.Contain("InstallerSha256"));
    }

    [Test]
    public void PrefersX64OverArm64()
    {
        const string yaml = """
        PackageIdentifier: Vendor.App
        PackageVersion: 1.2.3
        Installers:
        - Architecture: arm64
          InstallerUrl: https://example.com/app-arm64.exe
          InstallerSha256: BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
        - Architecture: x64
          InstallerUrl: https://example.com/app-x64.exe
          InstallerSha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
        """;

        var (manifest, error) = WingetManifestParser.Parse(yaml);

        Assert.That(error, Is.Null);
        Assert.That(manifest!.Installer.InstallerUrl, Does.Contain("x64"));
    }
}

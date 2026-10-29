using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Validação contra o repositório REAL (WU-21): confirma que o caminho
/// determinístico e o parsing continuam válidos contra o winget-pkgs ao vivo.
/// Gated por DISCOVERY_WINGET_SMOKE=1 (usa rede pública).
/// </summary>
[TestFixture]
public class WingetManifestSourceSmokeTests
{
    [Test]
    public async Task FetchesLatestChromeManifestFromRealRepository()
    {
        if (Environment.GetEnvironmentVariable("DISCOVERY_WINGET_SMOKE") != "1")
            Assert.Ignore("defina DISCOVERY_WINGET_SMOKE=1 para validar contra o GitHub");

        var source = new WingetManifestSource();
        var version = await source.GetLatestVersionAsync("Google.Chrome");
        Assert.That(version, Is.Not.Null.And.Not.Empty, "winget-pkgs deve listar versões de Google.Chrome");

        var yaml = await source.GetInstallerManifestAsync("Google.Chrome", version!);
        Assert.That(yaml, Is.Not.Null.And.Not.Empty, "manifesto .installer.yaml deve existir");

        var (manifest, error) = Discovery.Core.Catalog.WingetManifestParser.Parse(yaml);
        Assert.That(error, Is.Null);
        Assert.That(manifest!.PackageIdentifier, Is.EqualTo("Google.Chrome"));
        Assert.That(manifest.PackageVersion, Is.EqualTo(version));
        Assert.That(manifest.Installer.InstallerUrl, Does.StartWith("https://"));
        Assert.That(manifest.Installer.Sha256, Has.Length.EqualTo(64));
    }
}

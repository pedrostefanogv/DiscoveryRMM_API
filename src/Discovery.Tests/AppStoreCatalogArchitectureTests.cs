using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Rótulo de arquitetura do catálogo (badge da Loja de Softwares).
///
/// O DTO não expunha arquitetura por pacote, então o badge nunca aparecia; o
/// rótulo agora é derivado das chaves de installerUrlsByArch do manifesto.
/// O caso "neutral" (instalador que serve qualquer arquitetura) sai como
/// "todas" em vez do token cru do winget.
/// </summary>
[TestFixture]
public class AppStoreCatalogArchitectureTests
{
    private static async Task<AppCatalogPackageDto> LoadAsync(string? metadataJson)
    {
        await using var db = ApprovalRuleTestDbContext.New();
        db.AppPackages.Add(new AppPackage
        {
            Id = Guid.NewGuid(),
            InstallationType = AppInstallationType.Custom,
            PackageId = "pacote",
            Name = "Pacote",
            InstallCommand = "\"{{installer}}\" /S",
            MetadataJson = metadataJson,
        });
        await db.SaveChangesAsync();

        var service = new AppStoreService(null!, null!, null!, null!, null!, new AppPackageRepository(db), null!);
        var dto = await service.GetCatalogPackageByIdAsync(AppInstallationType.Custom, "pacote");

        Assert.That(dto, Is.Not.Null);
        return dto!;
    }

    [Test]
    public async Task Ordena_arquiteturas_e_traduz_neutral()
    {
        var multi = await LoadAsync(
            """{"installerUrlsByArch":{"arm64":"https://exemplo/a.exe","x64":"https://exemplo/b.exe"}}""");
        Assert.That(multi.Architecture, Is.EqualTo("x64, arm64"));

        var neutral = await LoadAsync(
            """{"installerUrlsByArch":{"neutral":"https://exemplo/c.exe"}}""");
        Assert.That(neutral.Architecture, Is.EqualTo("todas"));
    }

    [Test]
    public async Task Pacote_sem_instalador_por_arquitetura_fica_sem_rotulo()
    {
        var semMetadata = await LoadAsync(null);
        Assert.That(semMetadata.Architecture, Is.Empty);

        var metadataIrrelevante = await LoadAsync("""{"tags":["util"]}""");
        Assert.That(metadataIrrelevante.Architecture, Is.Empty);
    }

    /// <summary>
    /// Fim-a-fim com o produtor real do metadata: o ManifestParser do winget
    /// grava `installerUrlsByArch` (chaves minúsculas por arquitetura) e o DTO
    /// precisa ler exatamente essas chaves — uma divergência de nome aqui
    /// manteria o badge vazio para sempre, sem erro nenhum.
    /// </summary>
    [Test]
    public async Task Metadata_gerado_pelo_parser_do_winget_vira_rotulo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winget-manifest-arch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Installer.yaml"), """
                PackageIdentifier: Foxit.FoxitReader
                PackageVersion: 2026.1.3.36551
                Installers:
                  - Architecture: x64
                    InstallerUrl: https://example.com/foxit-x64.exe
                  - Architecture: x86
                    InstallerUrl: https://example.com/foxit-x86.exe
                """);
            File.WriteAllText(Path.Combine(dir, "DefaultLocale.yaml"), """
                PackageIdentifier: Foxit.FoxitReader
                PackageName: Foxit PDF Editor
                Publisher: Foxit Software
                """);

            var parsed = new WingetManifestParser().Parse("Foxit.FoxitReader", "2026.1.3.36551", dir);
            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed!.MetadataJson, Is.Not.Null);

            var dto = await LoadAsync(parsed.MetadataJson);
            Assert.That(dto.Architecture, Is.EqualTo("x64, x86"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

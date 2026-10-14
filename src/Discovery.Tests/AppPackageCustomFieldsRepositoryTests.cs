using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;

namespace Discovery.Tests;

/// <summary>
/// Persistência dos campos de detecção e tipo de comando (migration M223) no
/// <see cref="AppPackageRepository"/>, incluindo o clear do instalador. Usa o
/// provider InMemory com um contexto restrito a AppPackage (nenhum banco real).
/// </summary>
public class AppPackageCustomFieldsRepositoryTests
{
    private static DiscoveryDbContext NewDb() => AppPackageTestDbContext.New();

    private static AppPackage NewPackage(string packageId) => new()
    {
        InstallationType = AppInstallationType.Custom,
        PackageId = packageId,
        Name = "Foxit PDF Editor",
        InstallCommand = "{{installer}} /S",
        InstallCommandKind = AppCommandKind.PowerShellScript,
        UninstallCommandKind = AppCommandKind.CmdScript,
        DetectionMode = AppDetectionMode.InventoryLink,
        InventorySoftwareId = "6f0b2b7e-6a3b-4f0e-9d2a-1c2d3e4f5a6b",
        DetectionName = "Foxit PDF Editor",
        DetectionPublisher = "Foxit",
        DetectionMinVersion = "12.0",
        DetectionCommand = "Get-Process -Name FoxitPDFEditor -ErrorAction SilentlyContinue"
    };

    [Test]
    public async Task UpsertCustomAsync_create_persiste_os_8_campos()
    {
        await using var db = NewDb();
        var repo = new AppPackageRepository(db);

        await repo.UpsertCustomAsync(NewPackage("foxit"));

        var saved = await repo.GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, "foxit");

        Assert.That(saved, Is.Not.Null, "o pacote deveria ter sido criado");
        Assert.Multiple(() =>
        {
            Assert.That(saved!.InstallCommandKind, Is.EqualTo(AppCommandKind.PowerShellScript));
            Assert.That(saved.UninstallCommandKind, Is.EqualTo(AppCommandKind.CmdScript));
            Assert.That(saved.DetectionMode, Is.EqualTo(AppDetectionMode.InventoryLink));
            Assert.That(saved.InventorySoftwareId, Is.EqualTo("6f0b2b7e-6a3b-4f0e-9d2a-1c2d3e4f5a6b"));
            Assert.That(saved.DetectionName, Is.EqualTo("Foxit PDF Editor"));
            Assert.That(saved.DetectionPublisher, Is.EqualTo("Foxit"));
            Assert.That(saved.DetectionMinVersion, Is.EqualTo("12.0"));
            Assert.That(saved.DetectionCommand, Does.Contain("FoxitPDFEditor"));
        });
    }

    [Test]
    public async Task UpsertCustomAsync_update_sobrescreve_os_8_campos()
    {
        await using var db = NewDb();
        var repo = new AppPackageRepository(db);

        await repo.UpsertCustomAsync(NewPackage("foxit"));

        var incoming = NewPackage("foxit");
        incoming.InstallCommandKind = AppCommandKind.Command;
        incoming.UninstallCommandKind = AppCommandKind.Command;
        incoming.DetectionMode = AppDetectionMode.Command;
        incoming.InventorySoftwareId = null;
        incoming.DetectionName = null;
        incoming.DetectionPublisher = null;
        incoming.DetectionMinVersion = null;
        incoming.DetectionCommand = "exit 0";

        await repo.UpsertCustomAsync(incoming);

        var saved = await repo.GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, "foxit");

        Assert.That(saved, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(saved!.InstallCommandKind, Is.EqualTo(AppCommandKind.Command));
            Assert.That(saved.UninstallCommandKind, Is.EqualTo(AppCommandKind.Command));
            Assert.That(saved.DetectionMode, Is.EqualTo(AppDetectionMode.Command));
            Assert.That(saved.InventorySoftwareId, Is.Null);
            Assert.That(saved.DetectionName, Is.Null, "os metadados de detecção são sobrescritos sempre");
            Assert.That(saved.DetectionPublisher, Is.Null);
            Assert.That(saved.DetectionMinVersion, Is.Null);
            Assert.That(saved.DetectionCommand, Is.EqualTo("exit 0"));
        });
    }

    [Test]
    public async Task ClearCustomFileAsync_zera_o_instalador_e_devolve_o_estado_anterior()
    {
        await using var db = NewDb();
        var repo = new AppPackageRepository(db);

        var package = NewPackage("foxit");
        package.FileObjectKey = "app-store/custom/installers/abc/Setup.exe";
        package.FileBucket = "discovery";
        package.FilePublicUrl = "https://storage.invalid/Setup.exe";
        package.FileContentType = "application/x-msdownload";
        package.FileSizeBytes = 4096;
        package.FileChecksum = "abc123";
        package.FileName = "Setup.exe";
        await repo.UpsertCustomAsync(package);

        var previous = await repo.ClearCustomFileAsync("foxit");

        // O snapshot anterior é o que o handler usa para apagar o objeto no storage.
        Assert.That(previous, Is.Not.Null);
        Assert.That(previous!.FileObjectKey, Is.EqualTo("app-store/custom/installers/abc/Setup.exe"));

        var saved = await repo.GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, "foxit");
        Assert.That(saved, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(saved!.FileObjectKey, Is.Null);
            Assert.That(saved.FileBucket, Is.Null);
            Assert.That(saved.FilePublicUrl, Is.Null);
            Assert.That(saved.FileContentType, Is.Null);
            Assert.That(saved.FileSizeBytes, Is.Null);
            Assert.That(saved.FileChecksum, Is.Null);
            Assert.That(saved.FileName, Is.Null);

            // O pacote e os metadados continuam; só o arquivo foi removido.
            Assert.That(saved.Name, Is.EqualTo("Foxit PDF Editor"));
            Assert.That(saved.InstallCommandKind, Is.EqualTo(AppCommandKind.PowerShellScript));
            Assert.That(saved.DetectionMode, Is.EqualTo(AppDetectionMode.InventoryLink));
            Assert.That(saved.DetectionName, Is.EqualTo("Foxit PDF Editor"));
        });
    }

    [Test]
    public async Task ClearCustomFileAsync_pacote_inexistente_retorna_null()
    {
        await using var db = NewDb();
        var repo = new AppPackageRepository(db);

        var previous = await repo.ClearCustomFileAsync("nao-existe");

        Assert.That(previous, Is.Null);
    }

    [Test]
    public async Task ClearCustomFileAsync_nao_toca_pacote_de_outro_tipo()
    {
        await using var db = NewDb();
        var repo = new AppPackageRepository(db);

        // Pacote Winget com o mesmo packageId: o clear é exclusivo de Custom.
        await repo.BulkUpsertAsync(
            [
                new AppPackage
                {
                    PackageId = "winget-app",
                    Name = "Winget App",
                    FileObjectKey = "winget/installer.exe"
                }
            ],
            AppInstallationType.Winget);

        var previous = await repo.ClearCustomFileAsync("winget-app");

        Assert.That(previous, Is.Null);

        var untouched = await repo.GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Winget, "winget-app");
        Assert.That(untouched!.FileObjectKey, Is.EqualTo("winget/installer.exe"));
    }

    // Regressão: a URL do ícone é derivada pela API quando o ícone está no
    // storage (/custom/{id}/icon). Uma edição que não reenvia a URL zerava o
    // IconUrl e o catálogo ficava sem ícone mesmo com o objeto publicado.
    [Test]
    public async Task UpsertCustomAsync_preserva_IconUrl_derivada_quando_request_sem_url()
    {
        await using var db = NewDb();
        var repo = new AppPackageRepository(db);

        var created = NewPackage("foxit");
        created.IconObjectKey = "app-store/custom/icons/abc/logo.png";
        created.IconUrl = "/api/v1/app-store/custom/foxit/icon";
        await repo.UpsertCustomAsync(created);

        // Edição de metadados: sem novo upload e sem reenviar a URL derivada.
        var incoming = NewPackage("foxit");
        incoming.Name = "Foxit PDF Editor 2024";
        incoming.IconObjectKey = null;
        incoming.IconUrl = null;
        await repo.UpsertCustomAsync(incoming);

        var saved = await repo.GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, "foxit");

        Assert.That(saved, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(saved!.Name, Is.EqualTo("Foxit PDF Editor 2024"), "os metadados devem ser atualizados");
            Assert.That(saved.IconObjectKey, Is.EqualTo("app-store/custom/icons/abc/logo.png"));
            Assert.That(saved.IconUrl, Is.EqualTo("/api/v1/app-store/custom/foxit/icon"));
        });
    }

    // Contrapartida: ícone somente por URL externa (sem objeto no storage) pode
    // ser limpo quando o request não manda URL — não há chave a preservar.
    [Test]
    public async Task UpsertCustomAsync_limpa_IconUrl_externa_quando_nao_ha_icone_no_storage()
    {
        await using var db = NewDb();
        var repo = new AppPackageRepository(db);

        var created = NewPackage("xpto");
        created.IconUrl = "https://fornecedor.invalid/logo.png";
        await repo.UpsertCustomAsync(created);

        var incoming = NewPackage("xpto");
        incoming.IconUrl = null;
        await repo.UpsertCustomAsync(incoming);

        var saved = await repo.GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, "xpto");

        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.IconUrl, Is.Null);
    }
}

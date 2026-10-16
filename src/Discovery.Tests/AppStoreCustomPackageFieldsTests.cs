using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Serviço de pacotes custom da loja: mapeamento dos 8 campos novos (M223) no
/// upsert, validações dos modos de detecção e exclusão do instalador publicado
/// (clear no catálogo + best-effort no object storage). Sem banco real.
/// </summary>
public class AppStoreCustomPackageFieldsTests
{
    private static DiscoveryDbContext NewDb() => AppPackageTestDbContext.New();

    /// <summary>
    /// As dependências não exercitadas pelo upsert/clear (aprovações, auditoria,
    /// configuração, sites e agents) são null! de propósito — mesmo padrão de
    /// P2pOverviewServiceTests/AiChatA2uiReemitTests.
    /// </summary>
    private static AppStoreService NewService(DiscoveryDbContext db, ICustomPackageAssetService assets)
        => new(null!, null!, null!, null!, null!, new AppPackageRepository(db), assets);

    private static UpsertCustomAppCatalogPackageRequest NewRequest(string packageId = "foxit") => new()
    {
        PackageId = packageId,
        Name = "Foxit PDF Editor",
        InstallCommand = "\"{{installer}}\" /S",
        UninstallCommand = "\"{{installer}}\" /U",
        InstallCommandKind = (int)AppCommandKind.PowerShellScript,
        UninstallCommandKind = (int)AppCommandKind.CmdScript,
        DetectionMode = (int)AppDetectionMode.InventoryLink,
        InventorySoftwareId = "6f0b2b7e-6a3b-4f0e-9d2a-1c2d3e4f5a6b",
        DetectionName = "  Foxit PDF Editor  ",
        DetectionPublisher = " Foxit ",
        DetectionMinVersion = " 12.0 ",
        DetectionCommand = "  exit 0  "
    };

    [Test]
    public async Task UpsertCustomCatalogPackageAsync_mapeia_os_8_campos()
    {
        await using var db = NewDb();
        var service = NewService(db, new CapturingAssets());

        var dto = await service.UpsertCustomCatalogPackageAsync(NewRequest());

        Assert.Multiple(() =>
        {
            Assert.That(dto.InstallCommandKind, Is.EqualTo((int)AppCommandKind.PowerShellScript));
            Assert.That(dto.UninstallCommandKind, Is.EqualTo((int)AppCommandKind.CmdScript));
            Assert.That(dto.DetectionMode, Is.EqualTo((int)AppDetectionMode.InventoryLink));
            Assert.That(dto.InventorySoftwareId, Is.EqualTo("6f0b2b7e-6a3b-4f0e-9d2a-1c2d3e4f5a6b"));
            Assert.That(dto.DetectionPublisher, Is.EqualTo("Foxit"), "valores são trimados");
            Assert.That(dto.DetectionMinVersion, Is.EqualTo("12.0"));
            Assert.That(dto.DetectionCommand, Is.EqualTo("exit 0"));
        });

        // Trim aplicado também ao nome de detecção (é ele que o agent casa com o inventário local).
        Assert.That(dto.DetectionName, Is.EqualTo("Foxit PDF Editor"));

        var saved = await new AppPackageRepository(db)
            .GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, "foxit");

        Assert.That(saved, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(saved!.InstallCommandKind, Is.EqualTo(AppCommandKind.PowerShellScript));
            Assert.That(saved.UninstallCommandKind, Is.EqualTo(AppCommandKind.CmdScript));
            Assert.That(saved.DetectionMode, Is.EqualTo(AppDetectionMode.InventoryLink));
            Assert.That(saved.DetectionName, Is.EqualTo("Foxit PDF Editor"));
        });
    }

    [Test]
    public async Task UpsertCustomCatalogPackageAsync_sem_deteccao_nao_exige_campos()
    {
        await using var db = NewDb();
        var service = NewService(db, new CapturingAssets());

        var request = NewRequest();
        request.DetectionMode = (int)AppDetectionMode.None;
        request.InventorySoftwareId = null;
        request.DetectionName = null;
        request.DetectionPublisher = null;
        request.DetectionMinVersion = null;
        request.DetectionCommand = null;

        var dto = await service.UpsertCustomCatalogPackageAsync(request);

        Assert.That(dto.DetectionMode, Is.EqualTo((int)AppDetectionMode.None));
        Assert.That(dto.DetectionName, Is.Null);
    }

    [Test]
    public async Task UpsertCustomCatalogPackageAsync_InventoryLink_sem_nome_falha()
    {
        await using var db = NewDb();
        var service = NewService(db, new CapturingAssets());

        var request = NewRequest();
        request.DetectionName = "   ";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.UpsertCustomCatalogPackageAsync(request));

        Assert.That(ex!.Message, Does.Contain("DetectionName"));
    }

    [Test]
    public async Task UpsertCustomCatalogPackageAsync_Command_sem_comando_falha()
    {
        await using var db = NewDb();
        var service = NewService(db, new CapturingAssets());

        var request = NewRequest();
        request.DetectionMode = (int)AppDetectionMode.Command;
        request.DetectionCommand = null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.UpsertCustomCatalogPackageAsync(request));

        Assert.That(ex!.Message, Does.Contain("DetectionCommand"));
    }

    [Test]
    public async Task UpsertCustomCatalogPackageAsync_detection_command_acima_de_2000_chars_falha()
    {
        await using var db = NewDb();
        var service = NewService(db, new CapturingAssets());

        var request = NewRequest();
        request.DetectionMode = (int)AppDetectionMode.Command;
        request.DetectionCommand = new string('x', 2001);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.UpsertCustomCatalogPackageAsync(request));

        Assert.That(ex!.Message, Does.Contain("2000"));
    }

    [Test]
    public async Task UpsertCustomCatalogPackageAsync_detection_command_no_limite_e_aceito()
    {
        await using var db = NewDb();
        var service = NewService(db, new CapturingAssets());

        var request = NewRequest();
        request.DetectionMode = (int)AppDetectionMode.Command;
        request.DetectionCommand = new string('x', 2000);

        var dto = await service.UpsertCustomCatalogPackageAsync(request);

        Assert.That(dto.DetectionCommand, Has.Length.EqualTo(2000));
    }

    [Test]
    public async Task UpsertCustomCatalogPackageAsync_script_de_instalacao_longo_e_aceito()
    {
        await using var db = NewDb();
        var service = NewService(db, new CapturingAssets());

        // Script PowerShell real (pacote "Foxit PDF Editor"): ~9,5 mil caracteres.
        // Antes da M227 a coluna era varchar(1000) e isto era recusado/truncado.
        var request = NewRequest();
        request.InstallCommand = new string('x', 9_500);

        var dto = await service.UpsertCustomCatalogPackageAsync(request);

        Assert.That(dto.InstallCommand, Has.Length.EqualTo(9_500));
    }

    [Test]
    public async Task UpsertCustomCatalogPackageAsync_command_acima_do_limite_falha()
    {
        await using var db = NewDb();
        var service = NewService(db, new CapturingAssets());

        var request = NewRequest();
        request.InstallCommand = new string('x', AppStoreService.MaxCustomCommandLength + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.UpsertCustomCatalogPackageAsync(request));

        Assert.That(ex!.Message, Does.Contain(AppStoreService.MaxCustomCommandLength.ToString()));
    }

    [Test]
    [TestCase(-1, 0)]
    [TestCase(3, 0)]
    [TestCase(0, 3)]
    [TestCase(0, -1)]
    public async Task UpsertCustomCatalogPackageAsync_tipo_ou_modo_invalido_falha(int installKind, int detectionMode)
    {
        await using var db = NewDb();
        var service = NewService(db, new CapturingAssets());

        var request = NewRequest();
        request.InstallCommandKind = installKind;
        request.DetectionMode = detectionMode;
        request.DetectionName = "Foxit PDF Editor";

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.UpsertCustomCatalogPackageAsync(request));
    }

    [Test]
    public async Task DeleteCustomPackageFileAsync_limpa_instalador_e_apaga_objeto_no_storage()
    {
        await using var db = NewDb();
        var assets = new CapturingAssets();
        var service = NewService(db, assets);

        var request = NewRequest();
        request.FileObjectKey = "app-store/custom/installers/abc/Setup.exe";
        request.FileBucket = "discovery";
        request.FileContentType = "application/x-msdownload";
        request.FileSizeBytes = 4096;
        request.FileChecksum = "abc123";
        request.FileName = "Setup.exe";
        var created = await service.UpsertCustomCatalogPackageAsync(request);
        Assert.That(created.HasFile, Is.True);

        var deleted = await service.DeleteCustomPackageFileAsync("foxit");

        Assert.That(deleted, Is.True);
        Assert.That(assets.DeletedKeys, Is.EqualTo(new[] { "app-store/custom/installers/abc/Setup.exe" }));

        var dto = await service.GetCatalogPackageByIdAsync(AppInstallationType.Custom, "foxit");
        Assert.That(dto, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dto!.HasFile, Is.False);
            Assert.That(dto.FileName, Is.Empty);
            Assert.That(dto.DetectionName, Is.EqualTo("Foxit PDF Editor"), "os metadados continuam");
        });
    }

    [Test]
    public async Task DeleteCustomPackageFileAsync_falha_no_storage_nao_desfaz_o_clear()
    {
        await using var db = NewDb();
        var assets = new CapturingAssets { ThrowOnDelete = true };
        var service = NewService(db, assets);

        var request = NewRequest();
        request.FileObjectKey = "app-store/custom/installers/abc/Setup.exe";
        await service.UpsertCustomCatalogPackageAsync(request);

        // Best-effort: objeto órfão no storage não pode quebrar a operação.
        var deleted = await service.DeleteCustomPackageFileAsync("foxit");

        Assert.That(deleted, Is.True);
        var dto = await service.GetCatalogPackageByIdAsync(AppInstallationType.Custom, "foxit");
        Assert.That(dto!.HasFile, Is.False);
    }

    [Test]
    public async Task DeleteCustomPackageFileAsync_pacote_inexistente_retorna_false()
    {
        await using var db = NewDb();
        var assets = new CapturingAssets();
        var service = NewService(db, assets);

        var deleted = await service.DeleteCustomPackageFileAsync("nao-existe");

        Assert.That(deleted, Is.False);
        Assert.That(assets.DeletedKeys, Is.Empty);
    }

    /// <summary>Captura as exclusões no storage e permite simular falha do provedor.</summary>
    private sealed class CapturingAssets : ICustomPackageAssetService
    {
        public List<string> DeletedKeys { get; } = [];
        public bool ThrowOnDelete { get; set; }

        public Task<CustomPackageAssetDto> UploadAsync(
            string kind, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomPackageAssetContent> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WriteToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDelete)
                throw new InvalidOperationException("storage indisponível");

            DeletedKeys.Add(objectKey);
            return Task.CompletedTask;
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Cqrs.AppStore.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AppStore;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressão do editor de pacote custom (2026-10-14): o editor enviava
/// <c>detectionMode</c>/<c>inventorySoftwareId</c>/<c>detectionName</c> e
/// <c>installCommandKind</c>, mas o <see cref="UpsertCustomAppPackageCommand"/>
/// não tinha esses parâmetros. O model binding do MVC descartava o payload e o
/// handler montava o request sem eles — o pacote era salvo com
/// DetectionMode=None e InventorySoftwareId nulo ("não salva o vínculo com o
/// inventário").
/// </summary>
[TestFixture]
public class AppStoreCustomPackageCommandTests
{
    /// <summary>Payload camelCase como o site realmente envia (Program.cs usa JsonSerializerDefaults.Web).</summary>
    private const string SitePayload = """
        {
          "packageId": "foxitedito",
          "name": "Foxit editor",
          "publisher": "Foxit Software Inc.",
          "version": "14.0.7.33751",
          "installCommand": "\"{{installer}}\" /S",
          "uninstallCommand": "\"{{installer}}\" /U",
          "installCommandKind": 2,
          "uninstallCommandKind": 0,
          "extractZip": false,
          "detectionMode": 1,
          "inventorySoftwareId": "01a1312e-e874-7ea2-856f-261ef8aee7ce",
          "detectionName": "Foxit PDF Editor",
          "detectionPublisher": "Foxit Software Inc.",
          "detectionMinVersion": "14.0",
          "iconObjectKey": null,
          "fileObjectKey": null
        }
        """;

    private static JsonSerializerOptions ApiJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static AppStoreService NewService(DiscoveryDbContext db)
        => new(null!, null!, null!, null!, null!, new AppPackageRepository(db), new NoopAssets());

    private static AppStoreService NewService(DiscoveryDbContext db, ICustomPackageAssetService assets)
        => new(null!, null!, null!, null!, null!, new AppPackageRepository(db), assets);

    [Test]
    public async Task Upsert_rejeita_instalador_que_sumiu_do_storage()
    {
        // A limpeza descarta uploads não vinculados após 24h. Sem esta checagem o
        // pacote seria salvo apontando para um objeto inexistente e a instalação
        // só falharia lá no agent.
        await using var db = AppPackageTestDbContext.New();
        var service = NewService(db, new FixedAssetPresenceService(exists: false));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.UpsertCustomCatalogPackageAsync(new UpsertCustomAppCatalogPackageRequest
            {
                PackageId = "sumiu",
                Name = "App XPTO",
                InstallCommand = "setup.exe /S",
                FileObjectKey = "app-store/custom/installers/abc/Setup.exe",
                FileName = "Setup.exe",
            }, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("mais disponível"));
    }

    [Test]
    public async Task Upsert_aceita_instalador_presente_no_storage()
    {
        await using var db = AppPackageTestDbContext.New();
        var service = NewService(db, new FixedAssetPresenceService(exists: true));

        var saved = await service.UpsertCustomCatalogPackageAsync(new UpsertCustomAppCatalogPackageRequest
        {
            PackageId = "presente",
            Name = "App XPTO",
            InstallCommand = "setup.exe /S",
            FileObjectKey = "app-store/custom/installers/abc/Setup.exe",
            FileName = "Setup.exe",
        }, CancellationToken.None);

        Assert.That(saved.Name, Is.EqualTo("App XPTO"));
        var row = await new AppPackageRepository(db)
            .GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, "presente");
        Assert.That(row!.FileObjectKey, Is.EqualTo("app-store/custom/installers/abc/Setup.exe"));
    }

    [Test]
    public void Command_binda_a_deteccao_do_payload_do_editor()
    {
        var cmd = JsonSerializer.Deserialize<UpsertCustomAppPackageCommand>(SitePayload, ApiJsonOptions());

        Assert.That(cmd, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(cmd!.DetectionMode, Is.EqualTo((int)AppDetectionMode.InventoryLink));
            Assert.That(cmd.InventorySoftwareId, Is.EqualTo("01a1312e-e874-7ea2-856f-261ef8aee7ce"));
            Assert.That(cmd.DetectionName, Is.EqualTo("Foxit PDF Editor"));
            Assert.That(cmd.DetectionPublisher, Is.EqualTo("Foxit Software Inc."));
            Assert.That(cmd.DetectionMinVersion, Is.EqualTo("14.0"));
            Assert.That(cmd.InstallCommandKind, Is.EqualTo((int)AppCommandKind.PowerShellScript));
        });
    }

    [Test]
    public async Task Handler_persiste_vinculo_com_o_inventario()
    {
        await using var db = AppPackageTestDbContext.New();
        var handler = new UpsertCustomAppPackageCommandHandler(NewService(db));

        var cmd = JsonSerializer.Deserialize<UpsertCustomAppPackageCommand>(SitePayload, ApiJsonOptions())!;

        var result = await handler.Handle(cmd, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.Not.Null);

        var saved = await new AppPackageRepository(db)
            .GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, "foxitedito");

        Assert.That(saved, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(saved!.DetectionMode, Is.EqualTo(AppDetectionMode.InventoryLink));
            Assert.That(saved.InventorySoftwareId, Is.EqualTo("01a1312e-e874-7ea2-856f-261ef8aee7ce"));
            Assert.That(saved.DetectionName, Is.EqualTo("Foxit PDF Editor"));
            Assert.That(saved.DetectionPublisher, Is.EqualTo("Foxit Software Inc."));
            Assert.That(saved.DetectionMinVersion, Is.EqualTo("14.0"));
            Assert.That(saved.InstallCommandKind, Is.EqualTo(AppCommandKind.PowerShellScript));
        });
    }

    [Test]
    public async Task Handler_persiste_deteccao_por_comando()
    {
        await using var db = AppPackageTestDbContext.New();
        var handler = new UpsertCustomAppPackageCommandHandler(NewService(db));

        var cmd = new UpsertCustomAppPackageCommand(
            PackageId: "detect-por-comando",
            Name: "App XPTO",
            InstallCommand: "setup.exe /silent",
            DetectionMode: (int)AppDetectionMode.Command,
            DetectionCommand: "Test-Path 'C:\\Program Files\\XPTO'");

        var result = await handler.Handle(cmd, CancellationToken.None);
        Assert.That(result.IsSuccess, Is.True);

        var saved = await new AppPackageRepository(db)
            .GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, "detect-por-comando");

        Assert.That(saved!.DetectionMode, Is.EqualTo(AppDetectionMode.Command));
        Assert.That(saved.DetectionCommand, Is.EqualTo("Test-Path 'C:\\Program Files\\XPTO'"));
        Assert.That(saved.InventorySoftwareId, Is.Null);
    }

    [Test]
    public async Task Upsert_apaga_o_instalador_substituido()
    {
        // Trocar o instalador de um pacote que já tinha arquivo deixava o objeto
        // antigo no storage para sempre (o índice de limpeza dele já tinha
        // expirado). O vínculo antigo tem de ser removido no salvamento.
        await using var db = AppPackageTestDbContext.New();
        var assets = new FixedAssetPresenceService(exists: true);
        var service = NewService(db, assets);

        await service.UpsertCustomCatalogPackageAsync(new UpsertCustomAppCatalogPackageRequest
        {
            PackageId = "troca",
            Name = "App XPTO",
            InstallCommand = "setup.exe /S",
            FileObjectKey = "app-store/custom/installers/antigo/Setup.exe",
            FileName = "Setup.exe",
        }, CancellationToken.None);

        await service.UpsertCustomCatalogPackageAsync(new UpsertCustomAppCatalogPackageRequest
        {
            PackageId = "troca",
            Name = "App XPTO",
            InstallCommand = "setup.exe /S",
            FileObjectKey = "app-store/custom/installers/novo/Setup.exe",
            FileName = "Setup.exe",
        }, CancellationToken.None);

        Assert.That(assets.DeletedKeys, Does.Contain("app-store/custom/installers/antigo/Setup.exe"));
        Assert.That(assets.DeletedKeys, Does.Not.Contain("app-store/custom/installers/novo/Setup.exe"));
    }

    [Test]
    public async Task Edicao_parcial_nao_apaga_o_instalador_vinculado()
    {
        // Regressão (2026-10-15): o editor só manda fileObjectKey quando o operador
        // sobe um arquivo NOVO; num "Salvar" de metadados o request chega com a
        // chave nula e o repositório PRESERVA a chave vinculada (edição parcial).
        // O serviço tratava isso como "arquivo substituído" e apagava do storage o
        // instalador que a linha continuava apontando — o pacote ficava publicado
        // sem arquivo e o download no agent falhava (406 no lugar de 404).
        await using var db = AppPackageTestDbContext.New();
        var assets = new FixedAssetPresenceService(exists: true);
        var service = NewService(db, assets);

        await service.UpsertCustomCatalogPackageAsync(new UpsertCustomAppCatalogPackageRequest
        {
            PackageId = "parcial",
            Name = "App XPTO",
            InstallCommand = "\"{{installer}}\" /S",
            FileObjectKey = "app-store/custom/installers/atual/Setup.zip",
            FileName = "Setup.zip",
            FileContentType = "application/x-zip-compressed",
            IconObjectKey = "app-store/custom/icons/atual/logo.png",
        }, CancellationToken.None);

        // Mesmo pacote, só metadados: request sem as chaves de asset.
        await service.UpsertCustomCatalogPackageAsync(new UpsertCustomAppCatalogPackageRequest
        {
            PackageId = "parcial",
            Name = "App XPTO (renomeado)",
            InstallCommand = "\"{{installer}}\" /S",
        }, CancellationToken.None);

        var row = await new AppPackageRepository(db)
            .GetByInstallationTypeAndPackageIdAsync(AppInstallationType.Custom, "parcial");

        Assert.Multiple(() =>
        {
            Assert.That(assets.DeletedKeys, Is.Empty, "edição parcial não pode apagar o asset vinculado");
            Assert.That(row!.FileObjectKey, Is.EqualTo("app-store/custom/installers/atual/Setup.zip"));
            Assert.That(row.IconObjectKey, Is.EqualTo("app-store/custom/icons/atual/logo.png"));
            Assert.That(row.FileContentType, Is.EqualTo("application/x-zip-compressed"));
            Assert.That(row.Name, Is.EqualTo("App XPTO (renomeado)"));
        });
    }

    /// <summary>Simula a presença (ou não) do objeto no storage.</summary>
    private sealed class FixedAssetPresenceService(bool exists) : ICustomPackageAssetService
    {
        public List<string> DeletedKeys { get; } = [];

        public Task<CustomPackageAssetDto?> DescribeCompletedAssetAsync(
            string objectKey, string fileName, string? knownChecksum = null, CancellationToken cancellationToken = default)
            => Task.FromResult(exists
                ? new CustomPackageAssetDto
                {
                    ObjectKey = objectKey,
                    Bucket = "discovery-files",
                    ContentType = "application/x-msdownload",
                    FileName = fileName,
                    SizeBytes = 1024,
                    Checksum = "abc",
                }
                : null);

        public Task<CustomPackageAssetDto> UploadAsync(
            string kind, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomPackageAssetContent> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WriteToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(objectKey);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopAssets : ICustomPackageAssetService
    {
        public Task<CustomPackageAssetDto> UploadAsync(
            string kind, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomPackageAssetContent> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WriteToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

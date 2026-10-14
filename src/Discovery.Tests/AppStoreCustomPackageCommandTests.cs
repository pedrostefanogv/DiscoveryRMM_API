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

using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Sanitização do object storage: mede o bucket e remove apenas os assets da loja
/// (app-store/custom/) sem vínculo. Os testes cobrem o inventário, o filtro de
/// idade, a preservação de tudo que não é da loja, falha parcial e a trava de
/// execução concorrente.
/// </summary>
public class ObjectStorageSanitizationServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static ObjectStorageSanitizationService NewService(
        FakeStorage storage,
        FakeAppPackageRepository packages)
        => new(new FakeStorageFactory(storage), packages, NullLogger<ObjectStorageSanitizationService>.Instance);

    private static StorageObjectInfo Item(string objectKey, long sizeBytes, DateTimeOffset lastModified)
        => new(objectKey, sizeBytes, lastModified);

    [Test]
    public async Task DryRun_informa_inventario_e_orfaos_sem_apagar_nada()
    {
        var storage = new FakeStorage(
            Item("app-store/custom/installers/vinculado/Setup.exe", 900, Now.AddDays(-5)),
            Item("app-store/custom/icons/orfao/logo.png", 2, Now.AddDays(-2)),
            Item("app-store/custom/installers/recente/Setup.msi", 4, Now.AddMinutes(-5)),
            Item("clients/1/reports/x/r.csv", 5, Now.AddDays(-10)));

        var packages = new FakeAppPackageRepository(["app-store/custom/installers/vinculado/Setup.exe"]);

        var report = await NewService(storage, packages).RunAsync(dryRun: true, checks: null, minAgeHours: 24);

        Assert.Multiple(() =>
        {
            Assert.That(report.DryRun, Is.True);
            Assert.That(report.TotalObjects, Is.EqualTo(4));
            Assert.That(report.TotalBytes, Is.EqualTo(911));
            Assert.That(report.MinAgeHours, Is.EqualTo(24));
            Assert.That(report.Inventory.Single(e => e.Prefix == "app-store/").Objects, Is.EqualTo(3));
            Assert.That(report.Inventory.Single(e => e.Prefix == "clients/").Bytes, Is.EqualTo(5));
        });

        var orphan = report.Checks.Single(c => c.Key == ObjectStorageSanitizationChecks.OrphanCustomAssets);
        Assert.Multiple(() =>
        {
            Assert.That(orphan.Examined, Is.EqualTo(3), "só app-store/custom/ entra na verificação");
            Assert.That(orphan.Found, Is.EqualTo(1), "o vinculado e o recente não são órfãos");
            Assert.That(orphan.Fixed, Is.EqualTo(0), "dry-run nunca apaga");
            Assert.That(orphan.FoundBytes, Is.EqualTo(2));
            Assert.That(orphan.Skipped, Is.EqualTo(1), "o objeto recente é preservado");
            Assert.That(orphan.Samples.Single(), Does.Contain("app-store/custom/icons/orfao/logo.png"));
        });

        Assert.That(storage.Deleted, Is.Empty);
    }

    [Test]
    public async Task Aplicar_remove_so_orfaos_da_loja_com_idade_minima()
    {
        var storage = new FakeStorage(
            Item("app-store/custom/installers/velho/Setup.zip", 1_000, Now.AddDays(-4)),
            Item("app-store/custom/icons/orfao/logo.png", 300, Now.AddDays(-3)),
            Item("app-store/custom/installers/vinculado/Setup.exe", 900, Now.AddDays(-5)),
            Item("app-store/custom/installers/novo/Setup.msi", 700, Now.AddHours(-2)),
            Item("clients/9/attachments/keep.bin", 500, Now.AddDays(-30)));

        var packages = new FakeAppPackageRepository(["app-store/custom/installers/vinculado/Setup.exe"]);

        var report = await NewService(storage, packages).RunAsync(dryRun: false, checks: null, minAgeHours: 24);

        var orphan = report.Checks.Single(c => c.Key == ObjectStorageSanitizationChecks.OrphanCustomAssets);
        Assert.Multiple(() =>
        {
            Assert.That(orphan.Found, Is.EqualTo(2));
            Assert.That(orphan.Fixed, Is.EqualTo(2));
            Assert.That(orphan.FoundBytes, Is.EqualTo(1_300));
            Assert.That(orphan.FixedBytes, Is.EqualTo(1_300));
            Assert.That(report.TotalFixed, Is.EqualTo(2));
            Assert.That(report.TotalFixedBytes, Is.EqualTo(1_300));
        });

        Assert.That(storage.Deleted, Is.EquivalentTo(new[]
        {
            "app-store/custom/installers/velho/Setup.zip",
            "app-store/custom/icons/orfao/logo.png",
        }));
        // Nunca toca em anexo/relatório de cliente, mesmo antigo e sem vínculo.
        Assert.That(storage.Deleted, Does.Not.Contain("clients/9/attachments/keep.bin"));
    }

    [Test]
    public async Task Idade_minima_menor_permite_limpar_objetos_recentes()
    {
        var storage = new FakeStorage(
            Item("app-store/custom/installers/novo/Setup.msi", 700, Now.AddHours(-2)));

        var report = await NewService(storage, new FakeAppPackageRepository([]))
            .RunAsync(dryRun: false, checks: null, minAgeHours: 1);

        Assert.That(report.Checks.Single(c => c.Key == ObjectStorageSanitizationChecks.OrphanCustomAssets).Fixed, Is.EqualTo(1));
        Assert.That(storage.Deleted, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Data_de_modificacao_ausente_nao_e_tratada_como_lixo()
    {
        var storage = new FakeStorage(Item("app-store/custom/installers/sem-data/Setup.exe", 42, default) with { LastModifiedUtc = null });

        var report = await NewService(storage, new FakeAppPackageRepository([])).RunAsync(false, null, 24);
        var orphan = report.Checks.Single(c => c.Key == ObjectStorageSanitizationChecks.OrphanCustomAssets);

        Assert.Multiple(() =>
        {
            Assert.That(orphan.Found, Is.EqualTo(0));
            Assert.That(orphan.Skipped, Is.EqualTo(1));
            Assert.That(storage.Deleted, Is.Empty);
        });
    }

    [Test]
    public async Task Falha_ao_apagar_nao_aborta_a_limpeza_e_aparece_no_relatorio()
    {
        var storage = new FakeStorage(
            Item("app-store/custom/installers/a/Setup.zip", 100, Now.AddDays(-3)),
            Item("app-store/custom/installers/b/Setup.zip", 200, Now.AddDays(-3)))
        {
            FailOnDelete = "app-store/custom/installers/a/Setup.zip",
        };

        var report = await NewService(storage, new FakeAppPackageRepository([])).RunAsync(false, null, 24);
        var orphan = report.Checks.Single(c => c.Key == ObjectStorageSanitizationChecks.OrphanCustomAssets);

        Assert.Multiple(() =>
        {
            Assert.That(orphan.Found, Is.EqualTo(2));
            Assert.That(orphan.Fixed, Is.EqualTo(1), "o segundo objeto foi removido normalmente");
            Assert.That(orphan.FixedBytes, Is.EqualTo(200));
            Assert.That(orphan.Error, Does.Contain("installers/a/Setup.zip"));
        });
        Assert.That(storage.Deleted, Is.EquivalentTo(new[] { "app-store/custom/installers/b/Setup.zip" }));
    }

    [Test]
    public async Task Execucao_concorrente_e_bloqueada()
    {
        var gate = new TaskCompletionSource();
        var storage = new FakeStorage(Item("app-store/custom/installers/a/Setup.zip", 10, Now.AddDays(-3)))
        {
            ListGate = gate.Task,
        };

        var service = NewService(storage, new FakeAppPackageRepository([]));

        var first = service.RunAsync(dryRun: true, checks: null, minAgeHours: 24);
        await storage.ListStarted.Task;

        Assert.That(service.IsRunning, Is.True);
        await Assert.ThrowsAsync<ObjectStorageSanitizationBusyException>(
            () => service.RunAsync(dryRun: true, checks: null, minAgeHours: 24));

        gate.SetResult();
        await first;

        Assert.That(service.IsRunning, Is.False);
    }

    private sealed class FakeStorage(params StorageObjectInfo[] items) : IObjectStorageService
    {
        public List<string> Deleted { get; } = [];

        public string? FailOnDelete { get; init; }

        public Task? ListGate { get; init; }

        public TaskCompletionSource ListStarted { get; } = new();

        public async Task<ObjectStorageListing> ListObjectsAsync(
            string? prefix,
            int maxObjects,
            CancellationToken cancellationToken = default)
        {
            ListStarted.TrySetResult();

            if (ListGate is not null)
                await ListGate;

            var filtered = string.IsNullOrEmpty(prefix)
                ? items
                : items.Where(item => item.ObjectKey.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

            return new ObjectStorageListing(filtered, Truncated: false);
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            if (objectKey == FailOnDelete)
                throw new InvalidOperationException("storage fora do ar");

            Deleted.Add(objectKey);
            return Task.CompletedTask;
        }

        public Task<StorageObject> UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DownloadToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FakeStorageFactory(IObjectStorageService storage) : IObjectStorageProviderFactory
    {
        public IObjectStorageService CreateObjectStorageService() => storage;

        public Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(storage);

        public Task<List<string>> ValidateConfigurationAsync() => Task.FromResult(new List<string>());

        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ObjectStorageTestResult(true, true, true, [], 0));
    }

    private sealed class FakeAppPackageRepository(IReadOnlyCollection<string> referenced) : IAppPackageRepository
    {
        public Task<IReadOnlySet<string>> GetReferencedAssetKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(referenced, StringComparer.Ordinal));

        public Task<AppPackage?> GetByInstallationTypeAndPackageIdAsync(AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AppPackage>> GetByInstallationTypeAndPackageIdsAsync(AppInstallationType installationType, IReadOnlyCollection<string> packageIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AppPackage> Items, int TotalCount)> SearchPageAsync(AppInstallationType installationType, string? search, string? architecture, string? cursor, int limit, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AppPackage>> GetAllByInstallationTypeAsync(AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> BulkUpsertAsync(IReadOnlyList<AppPackage> packages, AppInstallationType installationType, CancellationToken cancellationToken = default, bool preventDowngrade = false) => throw new NotImplementedException();
        public Task<AppPackage> UpsertCustomAsync(AppPackage package, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> DeleteCustomAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AppPackage?> ClearCustomFileAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AppPackage?> ClearCustomIconAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> IsAssetObjectKeyReferencedAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}

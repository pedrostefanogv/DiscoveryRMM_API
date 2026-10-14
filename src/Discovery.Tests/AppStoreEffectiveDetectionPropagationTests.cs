using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// O payload efetivo enviado ao agent precisa carregar os 8 campos novos (M223)
/// tanto na política PreApproved (BuildEffectiveApprovedAppsAsync) quanto na All
/// (BuildAllCatalogAsEffectiveAppsAsync) — sem eles o agent não sabe executar
/// scripts nem decidir se o pacote custom já está instalado.
/// </summary>
public class AppStoreEffectiveDetectionPropagationTests
{
    private static DiscoveryDbContext NewDb() => AppPackageTestDbContext.New();

    private static async Task<DiscoveryDbContext> SeedPackageAsync(string packageId)
    {
        var db = NewDb();
        await new AppPackageRepository(db).UpsertCustomAsync(new AppPackage
        {
            InstallationType = AppInstallationType.Custom,
            PackageId = packageId,
            Name = "Foxit PDF Editor",
            Version = "12.0",
            Publisher = "Foxit",
            InstallCommand = "\"{{installer}}\" /S",
            InstallCommandKind = AppCommandKind.PowerShellScript,
            UninstallCommandKind = AppCommandKind.CmdScript,
            DetectionMode = AppDetectionMode.InventoryLink,
            InventorySoftwareId = "6f0b2b7e-6a3b-4f0e-9d2a-1c2d3e4f5a6b",
            DetectionName = "Foxit PDF Editor",
            DetectionPublisher = "Foxit",
            DetectionMinVersion = "12.0",
            DetectionCommand = "exit 0",
            FileObjectKey = "app-store/custom/installers/abc/Setup.exe",
            FileChecksum = "abc123",
            FileName = "Setup.exe",
            FileSizeBytes = 4096
        });
        return db;
    }

    private static void AssertFields(EffectiveApprovedAppDto app)
    {
        Assert.Multiple(() =>
        {
            Assert.That(app.InstallCommandKind, Is.EqualTo((int)AppCommandKind.PowerShellScript));
            Assert.That(app.UninstallCommandKind, Is.EqualTo((int)AppCommandKind.CmdScript));
            Assert.That(app.DetectionMode, Is.EqualTo((int)AppDetectionMode.InventoryLink));
            Assert.That(app.InventorySoftwareId, Is.EqualTo("6f0b2b7e-6a3b-4f0e-9d2a-1c2d3e4f5a6b"));
            Assert.That(app.DetectionName, Is.EqualTo("Foxit PDF Editor"));
            Assert.That(app.DetectionPublisher, Is.EqualTo("Foxit"));
            Assert.That(app.DetectionMinVersion, Is.EqualTo("12.0"));
            Assert.That(app.DetectionCommand, Is.EqualTo("exit 0"));
        });
    }

    [Test]
    public async Task GetEffectiveAppsAsync_PreApproved_propaga_os_8_campos()
    {
        await using var db = await SeedPackageAsync("foxit");

        var approvals = new FakeApprovalRules(
        [
            new AppApprovalRule
            {
                ScopeType = AppApprovalScopeType.Global,
                InstallationType = AppInstallationType.Custom,
                PackageId = "foxit",
                Action = AppApprovalActionType.Allow,
                UpdatedAt = DateTime.UtcNow
            }
        ]);

        var service = new AppStoreService(
            approvals,
            null!,
            new FakeConfigurationResolver(AppStorePolicyType.PreApproved),
            null!,
            null!,
            new AppPackageRepository(db),
            null!);

        var apps = await service.GetEffectiveAppsAsync(null, null, null, AppInstallationType.Custom);

        Assert.That(apps, Has.Count.EqualTo(1));
        Assert.That(apps[0].PackageId, Is.EqualTo("foxit"));
        AssertFields(apps[0]);
        Assert.That(apps[0].FileUrl, Is.Not.Empty, "com instalador publicado o agent recebe a URL do arquivo");
    }

    [Test]
    public async Task GetEffectiveAppsAsync_politica_All_propaga_os_8_campos()
    {
        await using var db = await SeedPackageAsync("foxit");

        var service = new AppStoreService(
            new FakeApprovalRules([]),
            null!,
            new FakeConfigurationResolver(AppStorePolicyType.All),
            null!,
            null!,
            new AppPackageRepository(db),
            null!);

        var apps = await service.GetEffectiveAppsAsync(null, null, null, AppInstallationType.Custom);

        Assert.That(apps, Has.Count.EqualTo(1));
        AssertFields(apps[0]);
    }

    private sealed class FakeApprovalRules(IReadOnlyList<AppApprovalRule> rules) : IAppApprovalRuleRepository
    {
        public Task<IReadOnlyList<AppApprovalRule>> GetByScopeAsync(
            AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType)
            => Task.FromResult<IReadOnlyList<AppApprovalRule>>(
                rules.Where(r => r.ScopeType == scopeType && r.InstallationType == installationType).ToList());

        public Task<AppApprovalRule?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppApprovalRule>> GetForInheritanceAsync(Guid clientId, Guid siteId, Guid agentId, AppInstallationType installationType) => throw new NotSupportedException();
        public Task<AppApprovalRule?> GetByUniqueKeyAsync(AppApprovalScopeType scopeType, Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, string packageId) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppApprovalRule>> GetByPackageAsync(AppInstallationType installationType, string packageId) => throw new NotSupportedException();
        public Task<AppApprovalRule> CreateAsync(AppApprovalRule rule) => throw new NotSupportedException();
        public Task UpdateAsync(AppApprovalRule rule) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class FakeConfigurationResolver(AppStorePolicyType policy) : IConfigurationResolver
    {
        public Task<ServerConfiguration> GetServerAsync()
            => Task.FromResult(new ServerConfiguration { AppStorePolicy = policy });

        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();
        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(Guid? clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => throw new NotSupportedException();
        public void ClearCache() { }
    }
}

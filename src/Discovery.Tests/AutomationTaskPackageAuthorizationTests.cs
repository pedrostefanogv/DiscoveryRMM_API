using System.Reflection;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regra de produto: a policy de automação SOBREPÕE a App Store. A tarefa é
/// ACEITA mesmo com pacote fora da loja — mas packageAuthorized=false e
/// packageWarning explícito são persistidos/expostos, inclusive para PackageId
/// inexistente no catálogo (ex. mozilla.thunderbird.br).
///
/// O catálogo consultado é o novo (app_packages): as tabelas legadas
/// winget_packages/chocolatey_packages foram removidas pela M055 e o mapeamento
/// EF delas foi excluído na limpeza pós-incidente de 2026-10-31.
/// </summary>
[TestFixture]
public class AutomationTaskPackageAuthorizationTests
{
    private static (bool? Authorized, string? Warning) Resolve(
        FakeAppStore store,
        AutomationTaskActionType actionType,
        AppInstallationType? installationType,
        string? packageId)
        => AutomationTaskService.ResolvePackageAuthorizationAsync(
            store, actionType, installationType, packageId,
            clientId: null, siteId: null, agentId: Guid.NewGuid()).GetAwaiter().GetResult();

    [Test]
    public void NonPackageAction_ReturnsNullAuthorization()
    {
        var (authorized, warning) = Resolve(new FakeAppStore(),
            AutomationTaskActionType.RunScript, null, null);
        Assert.That(authorized, Is.Null);
        Assert.That(warning, Is.Null);
    }

    [Test]
    public void PackageNotInStore_IsAcceptedWithWarning_PolicyOverridesStore()
    {
        // bleachbit.bleachbit existe no catálogo, mas não está na App Store efetiva.
        var store = new FakeAppStore(approved: ["google.chrome"], catalog: ["bleachbit.bleachbit", "google.chrome"]);
        var (authorized, warning) = Resolve(store,
            AutomationTaskActionType.UpdateOrInstallPackage, AppInstallationType.Winget, "bleachbit.bleachbit");
        Assert.That(authorized, Is.False);
        Assert.That(warning, Does.Contain("App Store"));
        Assert.That(warning, Does.Contain("policy de automação").IgnoreCase);
    }

    [Test]
    public void PackageIdNotInCatalog_WarnsExplicitlyWithSuggestion()
    {
        // mozilla.thunderbird.br não existe no catálogo; o correto é Mozilla.Thunderbird.pt-BR.
        var store = new FakeAppStore(approved: ["google.chrome"], catalog: ["google.chrome", "mozilla.thunderbird.pt-br"]);
        var (authorized, warning) = Resolve(store,
            AutomationTaskActionType.InstallPackage, AppInstallationType.Winget, "mozilla.thunderbird.br");
        Assert.That(authorized, Is.False);
        Assert.That(warning, Does.Contain("não existe no catálogo Winget"));
        Assert.That(warning, Does.Contain("Mozilla.Thunderbird.pt-BR"));
    }

    [Test]
    public void PackageInStore_IsAuthorizedWithoutWarning()
    {
        var store = new FakeAppStore(approved: ["GOOGLE.CHROME"], catalog: ["google.chrome"]);
        var (authorized, warning) = Resolve(store,
            AutomationTaskActionType.InstallPackage, AppInstallationType.Winget, "google.chrome");
        Assert.That(authorized, Is.True);
        Assert.That(warning, Is.Null);
    }

    [Test]
    public void EmptyCatalog_TreatedAsUnknown_DoesNotClaimMissingId()
    {
        // Catálogo não sincronizado (Total == 0): não se afirma que o id não existe.
        var store = new FakeAppStore(approved: ["google.chrome"]);
        var (authorized, warning) = Resolve(store,
            AutomationTaskActionType.InstallPackage, AppInstallationType.Winget, "google.chrome");
        Assert.That(authorized, Is.True);
        Assert.That(warning, Is.Null);
    }

    [Test]
    public void CustomPackageMissingFromCatalog_Warns()
    {
        var store = new FakeAppStore();
        var (authorized, warning) = Resolve(store,
            AutomationTaskActionType.InstallPackage, AppInstallationType.Custom, "meupacote.custom");
        Assert.That(authorized, Is.False);
        Assert.That(warning, Does.Contain("catálogo"));
    }

    [Test]
    public void CustomPackageInCatalogAndStore_IsAuthorized()
    {
        var store = new FakeAppStore(approved: ["meupacote.custom"], catalog: ["meupacote.custom"]);
        var (authorized, warning) = Resolve(store,
            AutomationTaskActionType.InstallPackage, AppInstallationType.Custom, "MeuPacote.Custom");
        Assert.That(authorized, Is.True);
        Assert.That(warning, Is.Null);
    }

    [Test]
    public void ExecutionResultEventPayload_CarriesExitCodeAndErrorMessage()
    {
        var payload = AutomationExecutionReportRepository.BuildExecutionResultPayload(
            Guid.NewGuid(), Guid.NewGuid(), AutomationExecutionStatus.Failed,
            Guid.NewGuid(), null, "corr-1", exitCode: 13,
            errorMessage: "pacote \"mozilla.thunderbird.br\" (Winget) não autorizado para este agent");

        var props = payload.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, p => p.GetValue(payload));

        Assert.That(props["exitCode"], Is.EqualTo(13));
        Assert.That(props["errorMessage"], Does.Contain("não autorizado para este agent"));
        Assert.That(props.ContainsKey("agentId"), Is.True);
        Assert.That(props.ContainsKey("status"), Is.True);
    }

    private sealed class FakeAppStore(IReadOnlyList<string>? approved = null, IReadOnlyList<string>? catalog = null) : IAppStoreService
    {
        private readonly IReadOnlyList<string> _approved = approved ?? [];
        private readonly HashSet<string> _catalog = new(catalog ?? [], StringComparer.OrdinalIgnoreCase);

        public Task<AppCatalogPackageDto?> GetCatalogPackageByIdAsync(AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(_catalog.Contains(packageId.Trim())
                ? new AppCatalogPackageDto { Id = packageId.Trim(), Name = packageId.Trim(), InstallationType = installationType }
                : null);

        public Task<bool> IsPackageApprovedAsync(Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(_approved.Any(a => string.Equals(a, packageId.Trim(), StringComparison.OrdinalIgnoreCase)));

        public Task<AppCatalogSearchResultDto> SearchCatalogAsync(AppInstallationType installationType, string? search, string? architecture, int limit, string? cursor, CancellationToken cancellationToken = default)
            => Task.FromResult(new AppCatalogSearchResultDto { TotalPackagesInSource = _catalog.Count });

        public Task<AppCatalogPackageDto> UpsertCustomCatalogPackageAsync(UpsertCustomAppCatalogPackageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteCustomPackageFileAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteCustomPackageIconAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalRuleResolvedDto> UpsertRuleAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, AppApprovalActionType action, bool? autoUpdateEnabled, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppApprovalRuleResolvedDto>> GetRulesByScopeAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<AppApprovalResolvedPageDto> GetResolvedRulesAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(Guid ruleId, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalAuditPageDto> GetAuditHistoryAsync(AppInstallationType installationType, string? packageId, AppApprovalScopeType? scopeType, Guid? scopeId, string? changedBy, DateTime? changedFrom, DateTime? changedTo, AppApprovalAuditChangeType? changeType, int limit, Guid? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EffectiveApprovedAppDto>> GetEffectiveAppsAsync(Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EffectiveApprovedAppPageDto> GetEffectiveAppsPageAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalPackageDiffDto> GetPackageDiffAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppEffectivePackageDiffPageDto> GetEffectiveAppDiffsAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

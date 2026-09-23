using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Software.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class SoftwareCommandHandlerTests
{
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly Guid InventoryId = Guid.NewGuid();

    private static AgentInstalledSoftware Installed(string source = "winget", string? packageId = "Google.Chrome")
        => new()
        {
            InventoryId = InventoryId,
            AgentId = AgentId,
            Name = "Google Chrome",
            Version = "120.0",
            InstallId = "{26A24AE4-039D-4CA4-87B4-2F83218035F0}",
            UpdatePackageId = packageId,
            UpdateSource = source,
            Source = "registry"
        };

    [Test]
    public async Task Update_Approved_DispatchesSoftwareUpdate()
    {
        var dispatcher = new FakeDispatcher();
        var reports = new FakeReportRepo();
        var handler = new UpdateAgentSoftwareCommandHandler(
            new FakeAgentRepo(), new FakeSiteRepo(), new FakeSoftwareRepo(Installed()),
            new FakeAppStore(approved: true), dispatcher, reports);

        var result = await handler.Handle(new UpdateAgentSoftwareCommand(AgentId, InventoryId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Last, Is.Not.Null);
        Assert.That(dispatcher.Last!.CommandType, Is.EqualTo(CommandType.SoftwareUpdate));
        Assert.That(reports.Created, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Update_Unapproved_WithoutConfirmation_ReturnsConflictAndDoesNotDispatch()
    {
        var dispatcher = new FakeDispatcher();
        var handler = new UpdateAgentSoftwareCommandHandler(
            new FakeAgentRepo(), new FakeSiteRepo(), new FakeSoftwareRepo(Installed()),
            new FakeAppStore(approved: false), dispatcher, new FakeReportRepo());

        var result = await handler.Handle(new UpdateAgentSoftwareCommand(AgentId, InventoryId), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Conflict"));
        Assert.That(dispatcher.Last, Is.Null);
    }

    [Test]
    public async Task Update_Unapproved_WithConfirmation_Dispatches()
    {
        var dispatcher = new FakeDispatcher();
        var handler = new UpdateAgentSoftwareCommandHandler(
            new FakeAgentRepo(), new FakeSiteRepo(), new FakeSoftwareRepo(Installed()),
            new FakeAppStore(approved: false), dispatcher, new FakeReportRepo());

        var result = await handler.Handle(
            new UpdateAgentSoftwareCommand(AgentId, InventoryId, ConfirmUnapproved: true), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Last!.CommandType, Is.EqualTo(CommandType.SoftwareUpdate));
    }

    [Test]
    public async Task Uninstall_DispatchesSoftwareUninstall()
    {
        var dispatcher = new FakeDispatcher();
        var handler = new UninstallAgentSoftwareCommandHandler(
            new FakeAgentRepo(), new FakeSoftwareRepo(Installed()), dispatcher, new FakeReportRepo());

        var result = await handler.Handle(new UninstallAgentSoftwareCommand(AgentId, InventoryId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Last!.CommandType, Is.EqualTo(CommandType.SoftwareUninstall));
    }

    private sealed class FakeAgentRepo : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id)
            => Task.FromResult<Agent?>(new Agent { Id = id, SiteId = Guid.NewGuid(), Hostname = "host" });
        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotSupportedException();
        public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, Discovery.Core.Enums.AgentStatus status, string? ipAddress) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => throw new NotSupportedException();
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeSiteRepo : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id)
            => Task.FromResult<Site?>(new Site { Id = id, ClientId = Guid.NewGuid(), Name = "site" });
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class FakeSoftwareRepo(AgentInstalledSoftware installed) : IAgentSoftwareRepository
    {
        public Task<AgentInstalledSoftware?> GetByInventoryIdAsync(Guid inventoryId)
            => Task.FromResult<AgentInstalledSoftware?>(installed.InventoryId == inventoryId ? installed : null);
        public Task<IEnumerable<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdPagedAsync(Guid agentId, string? cursor, int limit, string? search, bool descending) => throw new NotSupportedException();
        public Task<AgentSoftwarePageResult> GetCurrentByAgentIdOffsetAsync(Guid agentId, int page, int pageSize, string? search, bool descending, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentSoftwareSnapshot> GetSnapshotByAgentIdAsync(Guid agentId) => throw new NotSupportedException();
        public Task<int> GetUpdateAvailableCountByAgentIdAsync(Guid agentId) => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryGlobalPagedAsync(string? cursor, int limit, string? search, bool descending) => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryByClientPagedAsync(Guid clientId, string? cursor, int limit, string? search, bool descending) => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryBySitePagedAsync(Guid siteId, string? cursor, int limit, string? search, bool descending) => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogGlobalPagedAsync(string? cursor, int limit, string? search, bool descending) => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogByClientPagedAsync(Guid clientId, string? cursor, int limit, string? search, bool descending) => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogBySitePagedAsync(Guid siteId, string? cursor, int limit, string? search, bool descending) => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInstallationRow>> GetSoftwareInstallationsPagedAsync(Guid softwareId, Guid? clientId, Guid? siteId, string? cursor, int limit, bool descending) => throw new NotSupportedException();
        public Task<SoftwareInventoryScopeSnapshot> GetInventoryGlobalSnapshotAsync() => throw new NotSupportedException();
        public Task<SoftwareInventoryScopeSnapshot> GetInventoryByClientSnapshotAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SoftwareInventoryScopeSnapshot> GetInventoryBySiteSnapshotAsync(Guid siteId) => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareGlobalAsync(int limit) => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareBySiteAsync(Guid siteId, int limit) => throw new NotSupportedException();
        public Task ReplaceInventoryAsync(Guid agentId, DateTime collectedAt, IEnumerable<SoftwareInventoryEntry> software) => throw new NotSupportedException();
    }

    private sealed class FakeAppStore(bool approved) : IAppStoreService
    {
        public Task<bool> IsPackageApprovedAsync(Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(approved);
        public Task<AppCatalogSearchResultDto> SearchCatalogAsync(AppInstallationType installationType, string? search, string? architecture, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppCatalogPackageDto?> GetCatalogPackageByIdAsync(AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppCatalogPackageDto> UpsertCustomCatalogPackageAsync(UpsertCustomAppCatalogPackageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalRuleResolvedDto> UpsertRuleAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, AppApprovalActionType action, bool? autoUpdateEnabled, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppApprovalRuleResolvedDto>> GetRulesByScopeAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteRuleAsync(Guid ruleId, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalAuditPageDto> GetAuditHistoryAsync(AppInstallationType installationType, string? packageId, AppApprovalScopeType? scopeType, Guid? scopeId, string? changedBy, DateTime? changedFrom, DateTime? changedTo, AppApprovalAuditChangeType? changeType, int limit, Guid? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EffectiveApprovedAppDto>> GetEffectiveAppsAsync(Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EffectiveApprovedAppPageDto> GetEffectiveAppsPageAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalPackageDiffDto> GetPackageDiffAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppEffectivePackageDiffPageDto> GetEffectiveAppDiffsAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeDispatcher : IAgentCommandDispatcher
    {
        public AgentCommand? Last { get; private set; }

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            command.Id = Guid.NewGuid();
            Last = command;
            return Task.FromResult(command);
        }
    }

    private sealed class FakeReportRepo : IAutomationExecutionReportRepository
    {
        public List<AutomationExecutionReport> Created { get; } = [];

        public Task<AutomationExecutionReport> CreateAsync(AutomationExecutionReport report)
        {
            report.Id = Guid.NewGuid();
            Created.Add(report);
            return Task.FromResult(report);
        }

        public Task<AutomationExecutionReport?> GetByCommandIdAsync(Guid commandId) => throw new NotSupportedException();
        public Task<IReadOnlyList<AutomationExecutionReport>> GetByAgentIdAsync(Guid agentId, int limit = 100) => throw new NotSupportedException();
        public Task<IReadOnlyList<AutomationExecutionReport>> GetByTaskIdAsync(Guid taskId, int limit = 100) => throw new NotSupportedException();
        public Task UpdateAckAsync(Guid commandId, Guid? taskId, Guid? scriptId, string? ackMetadataJson, DateTime acknowledgedAt, string? correlationId) => throw new NotSupportedException();
        public Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId) => throw new NotSupportedException();
        public Task UpdateResultFromCommandAsync(Guid commandId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt) => throw new NotSupportedException();
        public Task UpsertPolicyExecutionAsync(Guid agentId, Guid commandId, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, AutomationExecutionStatus status, string? correlationId) => throw new NotSupportedException();
    }
}

using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.Misc;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

namespace Discovery.Tests;

/// <summary>
/// Payload de <c>me/app-store</c> (GetAppStoreEffectiveHandler): o agent Go lê os
/// campos novos como int/string camelCase, então o contrato JSON precisa sair
/// numérico (e não como o nome do enum, que o JsonStringEnumConverter global
/// produziria para propriedades tipadas como enum).
/// </summary>
public class AgentAppStoreEffectivePayloadTests
{
    [Test]
    public async Task Payload_do_agent_expoe_os_8_campos_em_camelCase()
    {
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var handler = new GetAppStoreEffectiveHandler(
            new FakeAgentRepository(new Agent { Id = agentId, SiteId = siteId, Hostname = "host" }),
            new StubConfigurationService(),
            new FakeSiteRepository(new Site { Id = siteId, ClientId = Guid.NewGuid(), Name = "site" }),
            new FakeAppStoreService());

        var result = await handler.Handle(
            new GetAppStoreEffectiveQuery(agentId, nameof(AppInstallationType.Custom)),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);

        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.GetProperty("items")[0];

        Assert.Multiple(() =>
        {
            var installKind = item.GetProperty("installCommandKind");
            Assert.That(installKind.ValueKind, Is.EqualTo(JsonValueKind.Number), "o agent lê int, não o nome do enum");
            Assert.That(installKind.GetInt32(), Is.EqualTo((int)AppCommandKind.PowerShellScript));

            Assert.That(item.GetProperty("uninstallCommandKind").GetInt32(), Is.EqualTo((int)AppCommandKind.CmdScript));
            Assert.That(item.GetProperty("detectionMode").GetInt32(), Is.EqualTo((int)AppDetectionMode.InventoryLink));
            Assert.That(item.GetProperty("inventorySoftwareId").GetString(), Is.EqualTo("6f0b2b7e-6a3b-4f0e-9d2a-1c2d3e4f5a6b"));
            Assert.That(item.GetProperty("detectionName").GetString(), Is.EqualTo("Foxit PDF Editor"));
            Assert.That(item.GetProperty("detectionPublisher").GetString(), Is.EqualTo("Foxit"));
            Assert.That(item.GetProperty("detectionMinVersion").GetString(), Is.EqualTo("12.0"));
            Assert.That(item.GetProperty("detectionCommand").GetString(), Is.EqualTo("exit 0"));
        });
    }

    private sealed class FakeAppStoreService : IAppStoreService
    {
        public Task<IReadOnlyList<EffectiveApprovedAppDto>> GetEffectiveAppsAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EffectiveApprovedAppDto>>(
            [
                new EffectiveApprovedAppDto
                {
                    InstallationType = AppInstallationType.Custom,
                    PackageId = "foxit",
                    Name = "Foxit PDF Editor",
                    InstallCommand = "\"{{installer}}\" /S",
                    InstallCommandKind = (int)AppCommandKind.PowerShellScript,
                    UninstallCommandKind = (int)AppCommandKind.CmdScript,
                    DetectionMode = (int)AppDetectionMode.InventoryLink,
                    InventorySoftwareId = "6f0b2b7e-6a3b-4f0e-9d2a-1c2d3e4f5a6b",
                    DetectionName = "Foxit PDF Editor",
                    DetectionPublisher = "Foxit",
                    DetectionMinVersion = "12.0",
                    DetectionCommand = "exit 0"
                }
            ]);

        public Task<AppCatalogSearchResultDto> SearchCatalogAsync(AppInstallationType installationType, string? search, string? architecture, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppCatalogPackageDto?> GetCatalogPackageByIdAsync(AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppCatalogPackageDto> UpsertCustomCatalogPackageAsync(UpsertCustomAppCatalogPackageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteCustomPackageFileAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalRuleResolvedDto> UpsertRuleAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, AppApprovalActionType action, bool? autoUpdateEnabled, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppApprovalRuleResolvedDto>> GetRulesByScopeAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteRuleAsync(Guid ruleId, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalAuditPageDto> GetAuditHistoryAsync(AppInstallationType installationType, string? packageId, AppApprovalScopeType? scopeType, Guid? scopeId, string? changedBy, DateTime? changedFrom, DateTime? changedTo, AppApprovalAuditChangeType? changeType, int limit, Guid? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsPackageApprovedAsync(Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EffectiveApprovedAppPageDto> GetEffectiveAppsPageAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalPackageDiffDto> GetPackageDiffAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppEffectivePackageDiffPageDto> GetEffectiveAppDiffsAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAgentRepository(Agent agent) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult<Agent?>(agent.Id == id ? agent : null);
        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotSupportedException();
        public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => throw new NotSupportedException();
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeSiteRepository(Site site) : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id) => Task.FromResult<Site?>(site.Id == id ? site : null);
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    /// <summary>Política padrão (PreApproved) — a loja fica habilitada para o agent.</summary>
    private sealed class StubConfigurationService : IConfigurationService
    {
        public Task<ServerConfiguration> GetServerConfigAsync() => Task.FromResult(new ServerConfiguration());
        public Task<ClientConfiguration?> GetClientConfigAsync(Guid clientId) => Task.FromResult<ClientConfiguration?>(null);
        public Task<SiteConfiguration?> GetSiteConfigAsync(Guid siteId) => Task.FromResult<SiteConfiguration?>(null);
        public Task<ServerConfiguration> PatchServerAsync(Dictionary<string, object> updates, string? updatedBy = null) => throw new NotSupportedException();
        public Task<ServerConfiguration> ResetServerAsync(string? resetBy = null) => throw new NotSupportedException();
        public Task<ClientConfiguration> CreateClientConfigAsync(Guid clientId, ClientConfiguration config, string? createdBy = null) => throw new NotSupportedException();
        public Task<ClientConfiguration> UpdateClientAsync(Guid clientId, ClientConfiguration config, string? updatedBy = null) => throw new NotSupportedException();
        public Task<ClientConfiguration> PatchClientAsync(Guid clientId, Dictionary<string, object> updates, string? updatedBy = null) => throw new NotSupportedException();
        public Task DeleteClientConfigAsync(Guid clientId, string? deletedBy = null) => throw new NotSupportedException();
        public Task ResetClientPropertyAsync(Guid clientId, string propertyName, string? resetBy = null) => throw new NotSupportedException();
        public Task<SiteConfiguration> CreateSiteConfigAsync(Guid siteId, SiteConfiguration config, string? createdBy = null) => throw new NotSupportedException();
        public Task<SiteConfiguration> UpdateSiteAsync(Guid siteId, SiteConfiguration config, string? updatedBy = null) => throw new NotSupportedException();
        public Task<SiteConfiguration> PatchSiteAsync(Guid siteId, Dictionary<string, object> updates, string? updatedBy = null) => throw new NotSupportedException();
        public Task DeleteSiteConfigAsync(Guid siteId, string? deletedBy = null) => throw new NotSupportedException();
        public Task ResetSitePropertyAsync(Guid siteId, string propertyName, string? resetBy = null) => throw new NotSupportedException();
        public Task<(bool IsValid, string[] Errors)> ValidateAsync(object config) => throw new NotSupportedException();
        public Task<(bool IsValid, string[] Errors)> ValidateJsonAsync(string objectType, string json) => throw new NotSupportedException();
    }
}

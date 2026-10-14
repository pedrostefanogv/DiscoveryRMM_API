using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.Misc;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

namespace Discovery.Tests;

/// <summary>
/// Contrato da paginação de <c>GET /api/v1/agent-auth/me/app-store</c>
/// (item 14 da análise de RAM do agent): a coleção efetiva pode passar de 27 mil
/// itens e a resposta única de ~30 MB obrigava o agent a bufferizar o corpo
/// inteiro. O servidor passa a fatiar quando recebe <c>paged=true</c>, mantendo
/// a resposta completa para os agents que não enviam o flag.
/// </summary>
public class AgentAppStorePaginationHandlerTests
{
    private static readonly Guid AgentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SiteId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static List<EffectiveApprovedAppDto> BuildApps(int count)
    {
        var apps = new List<EffectiveApprovedAppDto>(count);
        for (var i = 0; i < count; i++)
        {
            apps.Add(new EffectiveApprovedAppDto
            {
                InstallationType = AppInstallationType.Winget,
                PackageId = $"pkg-{i:D5}",
                Name = $"App {i:D5}",
                Version = "1.0.0"
            });
        }

        return apps;
    }

    private static GetAppStoreEffectiveHandler BuildHandler(IReadOnlyList<EffectiveApprovedAppDto> apps)
        => new(
            new FakeAgentRepository(new Agent { Id = AgentId, SiteId = SiteId, Hostname = "host" }),
            new StubConfigurationService(),
            new FakeSiteRepository(new Site { Id = SiteId, ClientId = Guid.NewGuid(), Name = "site" }),
            new FakeAppStoreService(apps));

    private static JsonElement Serialize(object? value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Test]
    public async Task Paged_Iteration_ReturnsEveryItemExactlyOnce()
    {
        const int total = 2505;
        const int pageSize = 1000;
        var handler = BuildHandler(BuildApps(total));

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        bool hasMore;

        do
        {
            var result = await handler.Handle(
                new GetAppStoreEffectiveQuery(AgentId, nameof(AppInstallationType.Winget), Paged: true, PageSize: pageSize, Cursor: cursor),
                CancellationToken.None);
            Assert.That(result.IsSuccess, Is.True);

            var payload = Serialize(result.Value);
            Assert.That(payload.GetProperty("count").GetInt32(), Is.EqualTo(total), "count é o total da política");
            Assert.That(payload.GetProperty("pageSize").GetInt32(), Is.EqualTo(pageSize));

            foreach (var item in payload.GetProperty("items").EnumerateArray())
                seen.Add(item.GetProperty("packageId").GetString()!);

            hasMore = payload.GetProperty("hasMore").GetBoolean();
            cursor = payload.GetProperty("nextCursor").GetString();
            pages++;

            if (hasMore)
                Assert.That(cursor, Is.Not.Empty, "hasMore exige nextCursor não vazio (senão o agent para cedo)");

            Assert.That(pages, Is.LessThan(50), "loop de paginação não terminou");
        } while (hasMore);

        Assert.That(pages, Is.EqualTo(3), "2505 itens / 1000 = 3 páginas");
        Assert.That(seen, Has.Count.EqualTo(total));
        Assert.That(seen.Distinct().Count(), Is.EqualTo(total), "nenhum item pode repetir entre páginas");
        Assert.That(seen[0], Is.EqualTo("pkg-00000"));
        Assert.That(seen[^1], Is.EqualTo("pkg-02504"));
    }

    [Test]
    public async Task Paged_LastPage_HasNoMoreAndEmptyCursor()
    {
        var handler = BuildHandler(BuildApps(10));

        var first = await handler.Handle(
            new GetAppStoreEffectiveQuery(AgentId, nameof(AppInstallationType.Winget), Paged: true, PageSize: 10),
            CancellationToken.None);
        var payload = Serialize(first.Value);

        Assert.That(payload.GetProperty("hasMore").GetBoolean(), Is.False);
        Assert.That(payload.GetProperty("nextCursor").GetString(), Is.Empty);
        Assert.That(payload.GetProperty("items").GetArrayLength(), Is.EqualTo(10));
    }

    [Test]
    public async Task LegacyMode_WithoutPaged_ReturnsEverythingInOnePage()
    {
        // Agents já instalados enviam apenas pageSize e têm limite de 20 páginas:
        // precisam continuar recebendo tudo numa página.
        var handler = BuildHandler(BuildApps(3000));

        var result = await handler.Handle(
            new GetAppStoreEffectiveQuery(AgentId, nameof(AppInstallationType.Winget), Paged: false, PageSize: 200),
            CancellationToken.None);
        var payload = Serialize(result.Value);

        Assert.That(payload.GetProperty("items").GetArrayLength(), Is.EqualTo(3000));
        Assert.That(payload.GetProperty("hasMore").GetBoolean(), Is.False);
        Assert.That(payload.GetProperty("nextCursor").GetString(), Is.Empty);
    }

    [Test]
    public async Task Paged_PageSizeIsClampedToServerCeiling()
    {
        var handler = BuildHandler(BuildApps(10));

        var result = await handler.Handle(
            new GetAppStoreEffectiveQuery(AgentId, nameof(AppInstallationType.Winget), Paged: true, PageSize: 999_999),
            CancellationToken.None);
        var payload = Serialize(result.Value);

        Assert.That(payload.GetProperty("pageSize").GetInt32(),
            Is.EqualTo(AppStoreEffectivePagination.MaxPageSize));
    }

    [Test]
    public async Task Paged_InvalidCursor_Fails()
    {
        var handler = BuildHandler(BuildApps(10));

        var result = await handler.Handle(
            new GetAppStoreEffectiveQuery(AgentId, nameof(AppInstallationType.Winget), Paged: true, PageSize: 5, Cursor: "nao-e-cursor"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False, "cursor inválido deve falhar em vez de repetir a primeira página");
    }

    [Test]
    public async Task Paged_CursorBeyondEnd_ReturnsEmptyPageWithoutMore()
    {
        var handler = BuildHandler(BuildApps(3));
        var cursor = AppStoreEffectivePagination.EncodeCursor(99);

        var result = await handler.Handle(
            new GetAppStoreEffectiveQuery(AgentId, nameof(AppInstallationType.Winget), Paged: true, PageSize: 5, Cursor: cursor),
            CancellationToken.None);
        var payload = Serialize(result.Value);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(payload.GetProperty("items").GetArrayLength(), Is.EqualTo(0));
        Assert.That(payload.GetProperty("hasMore").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Paged_OrderIsDeterministicAcrossRequests()
    {
        // A ordem do repositório não pode influenciar a fatia (o agent reusa o
        // cursor entre requisições).
        var apps = BuildApps(50);
        var shuffled = apps.OrderByDescending(a => a.PackageId, StringComparer.Ordinal).ToList();
        var handler = BuildHandler(shuffled);

        var result = await handler.Handle(
            new GetAppStoreEffectiveQuery(AgentId, nameof(AppInstallationType.Winget), Paged: true, PageSize: 10),
            CancellationToken.None);
        var payload = Serialize(result.Value);

        var ids = payload.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("packageId").GetString())
            .ToList();
        Assert.That(ids, Is.EqualTo(new[] { "pkg-00000", "pkg-00001", "pkg-00002", "pkg-00003", "pkg-00004", "pkg-00005", "pkg-00006", "pkg-00007", "pkg-00008", "pkg-00009" }));
    }

    [Test]
    public void Cursor_RoundTrips()
    {
        var cursor = AppStoreEffectivePagination.EncodeCursor(4321);
        Assert.That(AppStoreEffectivePagination.TryDecodeCursor(cursor, out var offset), Is.True);
        Assert.That(offset, Is.EqualTo(4321));

        Assert.That(AppStoreEffectivePagination.TryDecodeCursor(null, out var empty), Is.True);
        Assert.That(empty, Is.EqualTo(0));
        Assert.That(AppStoreEffectivePagination.TryDecodeCursor("###", out _), Is.False);
    }

    private sealed class FakeAppStoreService(IReadOnlyList<EffectiveApprovedAppDto> apps) : IAppStoreService
    {
        public Task<IReadOnlyList<EffectiveApprovedAppDto>> GetEffectiveAppsAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType,
            CancellationToken cancellationToken = default)
            => Task.FromResult(apps);

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

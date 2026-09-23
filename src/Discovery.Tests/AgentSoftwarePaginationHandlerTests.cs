using Discovery.Core.Cqrs.Agents.Inventory.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

namespace Discovery.Tests;

/// <summary>
/// Testes de contrato da paginação de software do agente:
///   - GetAgentSoftwareQueryHandler  (cursor keyset — regressão do loop infinito)
///   - GetAgentSoftwarePageQueryHandler (offset + total filtrado, usado pelo detalhe)
/// </summary>
public class AgentSoftwarePaginationHandlerTests
{
    private static readonly Guid AgentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static List<AgentInstalledSoftware> BuildInventory(int count)
    {
        // Ids sequenciais determinísticos (ordem estável asc/desc).
        var bytes = new byte[16];
        var list = new List<AgentInstalledSoftware>(count);
        for (var i = 0; i < count; i++)
        {
            bytes.AsSpan().Clear();
            bytes[15] = (byte)(i & 0xFF);
            bytes[14] = (byte)((i >> 8) & 0xFF);
            list.Add(new AgentInstalledSoftware
            {
                InventoryId = new Guid(bytes),
                AgentId = AgentId,
                SoftwareId = Guid.NewGuid(),
                Name = $"App {i:D4}",
                Version = "1.0.0",
                CollectedAt = DateTime.UtcNow,
                FirstSeenAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow
            });
        }

        return list;
    }

    // -------------------------------------------------------------------------
    // GetAgentSoftwareQueryHandler (cursor)
    // -------------------------------------------------------------------------

    [Test]
    public async Task GetSoftware_CursorEmitted_IsDecodableByHelper()
    {
        var inventory = BuildInventory(600);
        var agentRepo = new FakeAgentRepository(agent: new Agent { Id = AgentId });
        var softwareRepo = new FakeAgentSoftwareRepository(inventory);

        var handler = new GetAgentSoftwareQueryHandler(agentRepo, softwareRepo);
        var result = await handler.Handle(
            new GetAgentSoftwareQuery(AgentId, Cursor: null, Limit: 500), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.HasMore, Is.True);
        Assert.That(result.Value.Items, Has.Count.EqualTo(500));

        // REGRESSÃO do loop infinito: o cursor precisa decodificar via
        // TryDecodeGuidCursor e apontar para o ÚLTIMO ITEM EMITIDO (index 499
        // — usar a linha probe 501ª pularia 1 item por página).
        Assert.That(result.Value.NextCursor, Is.Not.Null);
        Assert.That(
            CursorPaginationHelper.TryDecodeGuidCursor(result.Value.NextCursor!, out var cursorId),
            Is.True,
            "nextCursor deve ser decodificável por TryDecodeGuidCursor (Base64 ou cru legado)");
        Assert.That(cursorId, Is.EqualTo(inventory[499].InventoryId));
    }

    [Test]
    public async Task GetSoftware_FullIteration_TerminatesWithoutDuplicates()
    {
        var inventory = BuildInventory(600);
        var agentRepo = new FakeAgentRepository(agent: new Agent { Id = AgentId });
        var softwareRepo = new FakeAgentSoftwareRepository(inventory);
        var handler = new GetAgentSoftwareQueryHandler(agentRepo, softwareRepo);

        var seen = new HashSet<Guid>();
        string? cursor = null;
        var requests = 0;

        while (requests < 10) // 600 itens / 500 por página = 2 páginas no máximo
        {
            var result = await handler.Handle(
                new GetAgentSoftwareQuery(AgentId, cursor, Limit: 500), CancellationToken.None);
            Assert.That(result.IsSuccess, Is.True);
            requests++;

            foreach (var item in result.Value!.Items)
            {
                Assert.That(seen.Add(item.InventoryId), Is.True, "item duplicado entre páginas");
            }

            if (!result.Value.HasMore || result.Value.NextCursor is null) break;
            cursor = result.Value.NextCursor;
        }

        Assert.That(requests, Is.LessThanOrEqualTo(3), "paginação deve terminar em poucas páginas");
        Assert.That(seen, Has.Count.EqualTo(600), "iteração deve cobrir todo o inventário");
    }

    [Test]
    public async Task GetSoftware_AgentMissing_ReturnsNotFound()
    {
        var handler = new GetAgentSoftwareQueryHandler(
            new FakeAgentRepository(agent: null), new FakeAgentSoftwareRepository([]));

        var result = await handler.Handle(
            new GetAgentSoftwareQuery(AgentId, null, 500), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    // -------------------------------------------------------------------------
    // GetAgentSoftwarePageQueryHandler (offset + total)
    // -------------------------------------------------------------------------

    [Test]
    public async Task GetSoftwarePage_ReturnsPageSliceAndTotals()
    {
        var inventory = BuildInventory(120);
        var handler = new GetAgentSoftwarePageQueryHandler(
            new FakeAgentRepository(agent: new Agent { Id = AgentId }),
            new FakeAgentSoftwareRepository(inventory));

        var result = await handler.Handle(
            new GetAgentSoftwarePageQuery(AgentId, Page: 2, PageSize: 50), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.TotalCount, Is.EqualTo(120));
        Assert.That(result.Value.TotalPages, Is.EqualTo(3));
        Assert.That(result.Value.Page, Is.EqualTo(2));
        Assert.That(result.Value.PageSize, Is.EqualTo(50));
        Assert.That(result.Value.Items, Has.Count.EqualTo(50));
        Assert.That(result.Value.Items[0].InventoryId, Is.EqualTo(inventory[50].InventoryId));
        Assert.That(result.Value.Items[^1].InventoryId, Is.EqualTo(inventory[99].InventoryId));
    }

    [Test]
    public async Task GetSoftwarePage_LastPartialPage_ReturnsRemainder()
    {
        var inventory = BuildInventory(120);
        var handler = new GetAgentSoftwarePageQueryHandler(
            new FakeAgentRepository(agent: new Agent { Id = AgentId }),
            new FakeAgentSoftwareRepository(inventory));

        var result = await handler.Handle(
            new GetAgentSoftwarePageQuery(AgentId, Page: 3, PageSize: 50), CancellationToken.None);

        Assert.That(result.Value!.Items, Has.Count.EqualTo(20));
        Assert.That(result.Value.Items[0].InventoryId, Is.EqualTo(inventory[100].InventoryId));
    }

    [Test]
    public async Task GetSoftwarePage_PageBeyondRange_ReturnsEmptyWithTotals()
    {
        var inventory = BuildInventory(120);
        var handler = new GetAgentSoftwarePageQueryHandler(
            new FakeAgentRepository(agent: new Agent { Id = AgentId }),
            new FakeAgentSoftwareRepository(inventory));

        var result = await handler.Handle(
            new GetAgentSoftwarePageQuery(AgentId, Page: 99, PageSize: 50), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Items, Is.Empty);
        Assert.That(result.Value.TotalCount, Is.EqualTo(120));
        Assert.That(result.Value.TotalPages, Is.EqualTo(3));
    }

    [Test]
    public async Task GetSoftwarePage_SearchFiltersTotalCount()
    {
        var inventory = BuildInventory(120);
        // Marca os nomes de 30 itens com o termo buscado.
        for (var i = 0; i < 30; i++) inventory[i].Name = $"Chrome {i:D4}";

        var handler = new GetAgentSoftwarePageQueryHandler(
            new FakeAgentRepository(agent: new Agent { Id = AgentId }),
            new FakeAgentSoftwareRepository(inventory));

        var result = await handler.Handle(
            new GetAgentSoftwarePageQuery(AgentId, Page: 1, PageSize: 50, Search: "chrome"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.TotalCount, Is.EqualTo(30), "total deve refletir o filtro de busca");
        Assert.That(result.Value.TotalPages, Is.EqualTo(1));
    }

    [Test]
    public async Task GetSoftwarePage_AgentMissing_ReturnsNotFound()
    {
        var handler = new GetAgentSoftwarePageQueryHandler(
            new FakeAgentRepository(agent: null), new FakeAgentSoftwareRepository([]));

        var result = await handler.Handle(
            new GetAgentSoftwarePageQuery(AgentId, 1, 50), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    // -------------------------------------------------------------------------
    // Fakes
    // -------------------------------------------------------------------------

    private sealed class FakeAgentRepository(Agent? agent) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult(agent);

        public Task<IEnumerable<Agent>> GetAllAsync() => Task.FromResult(Enumerable.Empty<Agent>());
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => Task.FromResult(Enumerable.Empty<Agent>());
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => Task.FromResult(Enumerable.Empty<Agent>());
        public Task<Agent> CreateAsync(Agent agent) => Task.FromResult(agent);
        public Task UpdateAsync(Agent agent) => Task.CompletedTask;
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([]);
        public Task ApproveZeroTouchAsync(Guid agentId) => Task.CompletedTask;
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => Task.CompletedTask;
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);
    }

    /// <summary>
    /// Fake que replica os contratos de paginação do repositório real
    /// (keyset por InventoryId asc/desc e offset com total).
    /// </summary>
    private sealed class FakeAgentSoftwareRepository(List<AgentInstalledSoftware> inventory) : IAgentSoftwareRepository
    {
        public Task<IEnumerable<AgentInstalledSoftware>> GetCurrentByAgentIdAsync(Guid agentId)
            => Task.FromResult(inventory.AsEnumerable());

        public Task<AgentInstalledSoftware?> GetByInventoryIdAsync(Guid inventoryId)
            => Task.FromResult(inventory.FirstOrDefault(x => x.InventoryId == inventoryId));

        public Task<IReadOnlyList<AgentInstalledSoftware>> GetCurrentByAgentIdPagedAsync(
            Guid agentId, string? cursor, int limit, string? search, bool descending)
        {
            IEnumerable<AgentInstalledSoftware> query = inventory;
            if (CursorPaginationHelper.TryDecodeGuidCursor(cursor, out var cursorId))
            {
                query = descending
                    ? query.Where(x => x.InventoryId.CompareTo(cursorId) < 0)
                    : query.Where(x => x.InventoryId.CompareTo(cursorId) > 0);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(x => x.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = descending
                ? query.OrderByDescending(x => x.InventoryId).ToList()
                : query.OrderBy(x => x.InventoryId).ToList();

            return Task.FromResult<IReadOnlyList<AgentInstalledSoftware>>(
                // Espelha o clamp do repo real: 501 = limite + probe de hasMore.
                ordered.Take(Math.Clamp(limit, 1, 501)).ToList());
        }

        public Task<AgentSoftwarePageResult> GetCurrentByAgentIdOffsetAsync(
            Guid agentId, int page, int pageSize, string? search, bool descending, CancellationToken ct = default)
        {
            IEnumerable<AgentInstalledSoftware> query = inventory;
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(x => x.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = descending
                ? query.OrderByDescending(x => x.InventoryId).ToList()
                : query.OrderBy(x => x.InventoryId).ToList();

            var safePage = Math.Max(1, page);
            var safePageSize = Math.Clamp(pageSize, 1, 2000);
            var items = ordered
                .Skip((safePage - 1) * safePageSize)
                .Take(safePageSize)
                .ToList();

            return Task.FromResult(new AgentSoftwarePageResult
            {
                Items = items,
                TotalCount = ordered.Count
            });
        }

        public Task<AgentSoftwareSnapshot> GetSnapshotByAgentIdAsync(Guid agentId)
            => Task.FromResult(new AgentSoftwareSnapshot { AgentId = agentId, TotalInstalled = inventory.Count });

        public Task<int> GetUpdateAvailableCountByAgentIdAsync(Guid agentId)
            => Task.FromResult(inventory.Count(x => x.UpdateAvailable));

        public Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryGlobalPagedAsync(string? cursor, int limit, string? search, bool descending)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryByClientPagedAsync(Guid clientId, string? cursor, int limit, string? search, bool descending)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryListItem>> GetInventoryBySitePagedAsync(Guid siteId, string? cursor, int limit, string? search, bool descending)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogGlobalPagedAsync(string? cursor, int limit, string? search, bool descending)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogByClientPagedAsync(Guid clientId, string? cursor, int limit, string? search, bool descending)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryCatalogItem>> GetInventoryCatalogBySitePagedAsync(Guid siteId, string? cursor, int limit, string? search, bool descending)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInstallationRow>> GetSoftwareInstallationsPagedAsync(Guid softwareId, Guid? clientId, Guid? siteId, string? cursor, int limit, bool descending)
            => throw new NotSupportedException();
        public Task<SoftwareInventoryScopeSnapshot> GetInventoryGlobalSnapshotAsync()
            => throw new NotSupportedException();
        public Task<SoftwareInventoryScopeSnapshot> GetInventoryByClientSnapshotAsync(Guid clientId)
            => throw new NotSupportedException();
        public Task<SoftwareInventoryScopeSnapshot> GetInventoryBySiteSnapshotAsync(Guid siteId)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareGlobalAsync(int limit)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SoftwareInventoryTopItem>> GetTopSoftwareBySiteAsync(Guid siteId, int limit)
            => throw new NotSupportedException();
        public Task ReplaceInventoryAsync(Guid agentId, DateTime collectedAt, IEnumerable<SoftwareInventoryEntry> software)
            => Task.CompletedTask;
    }
}

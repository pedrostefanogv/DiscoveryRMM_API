using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.SoftwareInventory.Queries;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.SoftwareInventory;

// ── Agent-scoped ─────────────────────────────────────────────────────
public sealed class ListAgentSoftwareQueryHandler(ISoftwareInventoryService svc) : IRequestHandler<ListAgentSoftwareQuery, Result<SoftwareInventoryDto>>
{
    public async Task<Result<SoftwareInventoryDto>> Handle(ListAgentSoftwareQuery q, CancellationToken ct)
    {
        var current = await svc.GetCurrentByAgentIdAsync(q.AgentId, ct);
        var snapshots = await svc.GetSnapshotsByAgentIdAsync(q.AgentId, ct);
        var items = current.Select(s => new SoftwareItemDto(s.InventoryId, s.Name, s.Version, s.Publisher, s.InstallDate?.ToString("o"), s.CollectedAt)).ToList().AsReadOnly();
        var snap = snapshots.FirstOrDefault();
        var snapDto = snap is not null ? new SnapshotDto(snap.AgentId, snap.TotalInstalled, snap.LastCollectedAt) : null;
        return Result<SoftwareInventoryDto>.Success(new SoftwareInventoryDto(items, snapDto));
    }
}

// ── Scope-based (global / client / site) ─────────────────────────────
public sealed class ListSoftwareInventoryQueryHandler(ISoftwareInventoryService svc) : IRequestHandler<ListSoftwareInventoryQuery, Result<SoftwareInventoryListDto>>
{
    public async Task<Result<SoftwareInventoryListDto>> Handle(ListSoftwareInventoryQuery q, CancellationToken ct)
    {
        var (clientId, siteId) = ResolveScope(q.Scope, q.ScopeId);

        // Fetch limit + 1 to detect hasMore
        var items = await svc.GetInventoryPagedAsync(clientId, siteId, q.Cursor, q.Limit + 1, q.Search, q.Descending, ct);

        var hasMore = items.Count > q.Limit;
        if (hasMore)
            items = items.Take(q.Limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextCursor = CursorPaginationHelper.EncodeGuidCursor(last.InventoryId);
        }

        var dtos = items.Select(i => new SoftwareInventoryItemDto(
            i.InventoryId, i.AgentId, i.SiteId, i.ClientId,
            i.SoftwareId, i.Name, i.Version, i.Publisher,
            i.InstallDate?.ToString("o"),
            i.Hostname, i.AgentDisplayName, i.SiteName, i.ClientName,
            i.CollectedAt
        )).ToList().AsReadOnly();

        return Result<SoftwareInventoryListDto>.Success(new SoftwareInventoryListDto(dtos, nextCursor, hasMore));
    }

    private static (Guid? clientId, Guid? siteId) ResolveScope(SoftwareInventoryScope scope, Guid? scopeId)
    {
        return scope switch
        {
            SoftwareInventoryScope.Client => (scopeId, null),
            SoftwareInventoryScope.Site => (null, scopeId),
            _ => (null, null)
        };
    }
}

public sealed class GetSoftwareInventoryScopeSnapshotQueryHandler(ISoftwareInventoryService svc) : IRequestHandler<GetSoftwareInventoryScopeSnapshotQuery, Result<ScopeSnapshotDto>>
{
    public async Task<Result<ScopeSnapshotDto>> Handle(GetSoftwareInventoryScopeSnapshotQuery q, CancellationToken ct)
    {
        Guid? clientId = null;
        Guid? siteId = null;

        switch (q.Scope)
        {
            case SoftwareInventoryScope.Client:
                clientId = q.ScopeId;
                break;
            case SoftwareInventoryScope.Site:
                siteId = q.ScopeId;
                break;
        }

        var snap = await svc.GetInventorySnapshotAsync(clientId, siteId, ct);

        return Result<ScopeSnapshotDto>.Success(new ScopeSnapshotDto(
            snap.TotalInstalled,
            snap.DistinctSoftware,
            snap.DistinctAgents,
            snap.LastCollectedAt
        ));
    }
}

// ── Agent-scoped snapshot (kept for agent-detail page) ───────────────
public sealed class GetSoftwareInventorySnapshotQueryHandler(ISoftwareInventoryService svc) : IRequestHandler<GetSoftwareInventorySnapshotQuery, Result<SnapshotDto>>
{
    public async Task<Result<SnapshotDto>> Handle(GetSoftwareInventorySnapshotQuery q, CancellationToken ct)
    {
        // This query has no agentId — returns global aggregate.
        // For the current page, use GetSoftwareInventoryScopeSnapshotQuery instead.
        var snap = await svc.GetInventorySnapshotAsync(null, null, ct);
        return Result<SnapshotDto>.Success(new SnapshotDto(Guid.Empty, snap.TotalInstalled, snap.LastCollectedAt));
    }
}
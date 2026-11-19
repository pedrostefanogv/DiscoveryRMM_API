using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.SoftwareInventory.Queries;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.SoftwareInventory;

internal static class SoftwareInventoryScopeGuard
{
    public static async Task<Result<(Guid? clientId, Guid? siteId)>> ResolveAuthorizedScopeAsync(
        SoftwareInventoryScope scope,
        Guid? scopeId,
        IScopeContext scopeContext,
        ISiteRepository siteRepo)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);

        switch (scope)
        {
            case SoftwareInventoryScope.Client:
                if (!scopeId.HasValue)
                    return Result<(Guid?, Guid?)>.Failure(Error.Validation("scopeId", "scopeId is required for Client scope."));
                if (!access.HasGlobalAccess && !access.AllowedClientIds.Contains(scopeId.Value))
                    return Result<(Guid?, Guid?)>.Failure(Error.Forbidden("Você não tem acesso a este cliente."));
                return Result<(Guid?, Guid?)>.Success((scopeId.Value, null));

            case SoftwareInventoryScope.Site:
                if (!scopeId.HasValue)
                    return Result<(Guid?, Guid?)>.Failure(Error.Validation("scopeId", "scopeId is required for Site scope."));
                var site = await siteRepo.GetByIdAsync(scopeId.Value);
                if (site is null)
                    return Result<(Guid?, Guid?)>.Failure(Error.NotFound("Site not found."));
                if (!TenantScopePolicy.Allows(access, site.ClientId, scopeId.Value))
                    return Result<(Guid?, Guid?)>.Failure(Error.Forbidden("Você não tem acesso a este site."));
                return Result<(Guid?, Guid?)>.Success((null, scopeId.Value));

            default:
                if (!access.HasGlobalAccess)
                    return Result<(Guid?, Guid?)>.Failure(Error.Forbidden("Acesso global ao inventário requer permissão global."));
                return Result<(Guid?, Guid?)>.Success((null, null));
        }
    }
}

// ── Agent-scoped ─────────────────────────────────────────────────────
public sealed class ListAgentSoftwareQueryHandler(
    ISoftwareInventoryService svc,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IScopeContext scopeContext
) : IRequestHandler<ListAgentSoftwareQuery, Result<SoftwareInventoryDto>>
{
    public async Task<Result<SoftwareInventoryDto>> Handle(ListAgentSoftwareQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<SoftwareInventoryDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return Result<SoftwareInventoryDto>.Failure(Error.Forbidden("Você não tem acesso a este agente."));

        var current = await svc.GetCurrentByAgentIdAsync(q.AgentId, ct);
        var snapshots = await svc.GetSnapshotsByAgentIdAsync(q.AgentId, ct);
        var items = current.Select(s => new SoftwareItemDto(s.InventoryId, s.Name, s.Version, s.Publisher, s.InstallDate?.ToString("o"), s.CollectedAt)).ToList().AsReadOnly();
        var snap = snapshots.FirstOrDefault();
        var snapDto = snap is not null ? new SnapshotDto(snap.AgentId, snap.TotalInstalled, snap.LastCollectedAt) : null;
        return Result<SoftwareInventoryDto>.Success(new SoftwareInventoryDto(items, snapDto));
    }
}

// ── Scope-based (global / client / site) ─────────────────────────────
public sealed class ListSoftwareInventoryQueryHandler(
    ISoftwareInventoryService svc,
    IScopeContext scopeContext,
    ISiteRepository siteRepo
) : IRequestHandler<ListSoftwareInventoryQuery, Result<SoftwareInventoryListDto>>
{
    public async Task<Result<SoftwareInventoryListDto>> Handle(ListSoftwareInventoryQuery q, CancellationToken ct)
    {
        var resolved = await SoftwareInventoryScopeGuard.ResolveAuthorizedScopeAsync(q.Scope, q.ScopeId, scopeContext, siteRepo);
        if (resolved.IsFailure)
            return Result<SoftwareInventoryListDto>.Failure(resolved.Errors);

        var (clientId, siteId) = resolved.Value;

        // Fetch limit + 1 to detect hasMore
        var items = await svc.GetCatalogPagedAsync(clientId, siteId, q.Cursor, q.Limit + 1, q.Search, q.Descending, ct);

        var hasMore = items.Count > q.Limit;
        if (hasMore)
            items = items.Take(q.Limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextCursor = CursorPaginationHelper.EncodeNameCursor(last.Name, last.SoftwareId);
        }

        var dtos = items.Select(i => new SoftwareInventoryCatalogItemDto(
            i.SoftwareId, i.Name, i.Publisher, i.Source,
            i.InstalledCount, i.FirstSeenAt, i.LastCollectedAt, i.LastSeenAt
        )).ToList().AsReadOnly();

        return Result<SoftwareInventoryListDto>.Success(new SoftwareInventoryListDto(dtos, nextCursor, hasMore));
    }
}

public sealed class ListSoftwareInstallationsQueryHandler(
    ISoftwareInventoryService svc,
    IScopeContext scopeContext,
    ISiteRepository siteRepo
) : IRequestHandler<ListSoftwareInstallationsQuery, Result<SoftwareInstallationsDto>>
{
    public async Task<Result<SoftwareInstallationsDto>> Handle(ListSoftwareInstallationsQuery q, CancellationToken ct)
    {
        var resolved = await SoftwareInventoryScopeGuard.ResolveAuthorizedScopeAsync(q.Scope, q.ScopeId, scopeContext, siteRepo);
        if (resolved.IsFailure)
            return Result<SoftwareInstallationsDto>.Failure(resolved.Errors);

        var (clientId, siteId) = resolved.Value;

        var items = await svc.GetSoftwareInstallationsPagedAsync(q.SoftwareId, clientId, siteId, q.Cursor, q.Limit + 1, q.Descending, ct);

        var hasMore = items.Count > q.Limit;
        if (hasMore)
            items = items.Take(q.Limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            // Cursor por software+agent não é possível com Guid simples; usamos o AgentId da última linha.
            nextCursor = CursorPaginationHelper.EncodeGuidCursor(items[^1].AgentId);
        }

        var dtos = items.Select(i => new SoftwareInstallationItemDto(
            i.AgentId, i.Hostname, i.AgentDisplayName,
            i.SiteId, i.SiteName, i.ClientId, i.ClientName,
            i.Version, i.Source,
            i.CollectedAt, i.FirstSeenAt, i.LastSeenAt
        )).ToList().AsReadOnly();

        return Result<SoftwareInstallationsDto>.Success(new SoftwareInstallationsDto(dtos, nextCursor, hasMore));
    }
}

public sealed class GetSoftwareInventoryScopeSnapshotQueryHandler(
    ISoftwareInventoryService svc,
    IScopeContext scopeContext,
    ISiteRepository siteRepo
) : IRequestHandler<GetSoftwareInventoryScopeSnapshotQuery, Result<ScopeSnapshotDto>>
{
    public async Task<Result<ScopeSnapshotDto>> Handle(GetSoftwareInventoryScopeSnapshotQuery q, CancellationToken ct)
    {
        var resolved = await SoftwareInventoryScopeGuard.ResolveAuthorizedScopeAsync(q.Scope, q.ScopeId, scopeContext, siteRepo);
        if (resolved.IsFailure)
            return Result<ScopeSnapshotDto>.Failure(resolved.Errors);

        var (clientId, siteId) = resolved.Value;

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
public sealed class GetSoftwareInventorySnapshotQueryHandler(
    ISoftwareInventoryService svc,
    IScopeContext scopeContext
) : IRequestHandler<GetSoftwareInventorySnapshotQuery, Result<SnapshotDto>>
{
    public async Task<Result<SnapshotDto>> Handle(GetSoftwareInventorySnapshotQuery q, CancellationToken ct)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);
        if (!access.HasGlobalAccess)
            return Result<SnapshotDto>.Failure(Error.Forbidden("Acesso global ao inventário requer permissão global."));

        // This query has no agentId — returns global aggregate.
        // For the current page, use GetSoftwareInventoryScopeSnapshotQuery instead.
        var snap = await svc.GetInventorySnapshotAsync(null, null, ct);
        return Result<SnapshotDto>.Success(new SnapshotDto(Guid.Empty, snap.TotalInstalled, snap.LastCollectedAt));
    }
}
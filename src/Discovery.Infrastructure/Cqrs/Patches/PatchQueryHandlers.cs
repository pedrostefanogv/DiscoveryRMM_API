using System.Text;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Patches;

// ── Helpers ────────────────────────────────────────────────────────────────

internal static class PatchScopeResolver
{
    public static (Guid? ClientId, Guid? SiteId) Resolve(PatchScope scope, Guid? scopeId) => scope switch
    {
        PatchScope.Client => (scopeId, null),
        PatchScope.Site => (null, scopeId),
        _ => (null, null)
    };

    public static PatchUpdateStatus? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "pending" => PatchUpdateStatus.Pending,
        "installed" => PatchUpdateStatus.Installed,
        "approved" => PatchUpdateStatus.Approved,
        "denied" => PatchUpdateStatus.Denied,
        "installing" => PatchUpdateStatus.Installing,
        "failed" => PatchUpdateStatus.Failed,
        "superseded" => PatchUpdateStatus.Superseded,
        _ => null
    };

    public static string EncodeKeyCursor(string groupKey)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(groupKey));
}

// ── Agente ────────────────────────────────────────────────────────────────

public sealed class GetAgentWindowsUpdatesPageQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetAgentWindowsUpdatesPageQuery, Result<AgentWindowsUpdatePageDto>>
{
    public async Task<Result<AgentWindowsUpdatePageDto>> Handle(
        GetAgentWindowsUpdatesPageQuery query,
        CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 2000);
        var status = PatchScopeResolver.ParseStatus(query.Status);

        var result = await repo.GetAgentUpdatesPageAsync(
            query.AgentId, page, pageSize, query.Search, status, query.Severity, query.Descending, ct);

        var dto = new AgentWindowsUpdatePageDto(
            result.Items.Select(PatchDtoMapper.ToDto).ToList(),
            result.TotalCount,
            page,
            pageSize,
            result.PendingCount,
            result.InstalledCount);

        return Result<AgentWindowsUpdatePageDto>.Success(dto);
    }
}

public sealed class GetAgentWindowsUpdatesSnapshotQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetAgentWindowsUpdatesSnapshotQuery, Result<AgentWindowsUpdateSnapshotDto>>
{
    public async Task<Result<AgentWindowsUpdateSnapshotDto>> Handle(
        GetAgentWindowsUpdatesSnapshotQuery query,
        CancellationToken ct)
    {
        var snapshot = await repo.GetSnapshotAsync(query.AgentId, ct);
        var pending = await repo.GetPendingForAgentAsync(query.AgentId, ct);

        var denied = pending.Count(update => update.ApprovalState == PatchApprovalState.Denied);
        var approved = pending.Count(update =>
            update.ApprovalState is PatchApprovalState.Approved or PatchApprovalState.AutoApproved);

        var dto = PatchDtoMapper.ToSnapshotDto(
            query.AgentId,
            snapshot,
            pending.Count,
            snapshot?.InstalledCount ?? 0,
            denied,
            approved);

        return Result<AgentWindowsUpdateSnapshotDto>.Success(dto with
        {
            DeniedCount = denied,
            ApprovedCount = approved
        });
    }
}

// ── Frota ─────────────────────────────────────────────────────────────────

public sealed class GetPatchInventoryQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchInventoryQuery, Result<PatchInventoryListDto>>
{
    public async Task<Result<PatchInventoryListDto>> Handle(GetPatchInventoryQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var (clientId, siteId) = PatchScopeResolver.Resolve(query.Scope, query.ScopeId);

        var items = await repo.GetInventoryGroupsAsync(
            clientId, siteId, query.Cursor, limit + 1, query.Search, query.Severity, query.Status,
            query.Descending, query.HasGlobalAccess, query.AllowedClientIds, ct);

        var hasMore = items.Count > limit;
        if (hasMore)
            items = items.Take(limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
            nextCursor = PatchScopeResolver.EncodeKeyCursor(items[^1].GroupKey);

        var dtos = items.Select(item => new PatchInventoryGroupDto(
            item.GroupKey,
            PatchJson.ParseStringArray(item.KbIds),
            item.Title,
            item.Classification,
            item.Severity,
            item.PublishedAt,
            item.PendingAgents,
            item.InstalledAgents,
            item.DeniedAgents,
            item.ReportingAgents,
            item.FirstSeenAt,
            item.LastSeenAt)).ToList();

        return Result<PatchInventoryListDto>.Success(new PatchInventoryListDto(dtos, nextCursor, hasMore));
    }
}

public sealed class GetPatchInventoryAgentsQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchInventoryAgentsQuery, Result<PatchInventoryAgentsDto>>
{
    public async Task<Result<PatchInventoryAgentsDto>> Handle(
        GetPatchInventoryAgentsQuery query,
        CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var (clientId, siteId) = PatchScopeResolver.Resolve(query.Scope, query.ScopeId);
        var status = PatchScopeResolver.ParseStatus(query.Status);

        var items = await repo.GetInventoryAgentsAsync(
            query.GroupKey, clientId, siteId, query.Cursor, limit + 1, status,
            query.HasGlobalAccess, query.AllowedClientIds, ct);

        var hasMore = items.Count > limit;
        if (hasMore)
            items = items.Take(limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
            nextCursor = Discovery.Core.Helpers.CursorPaginationHelper.EncodeGuidCursor(items[^1].AgentId);

        var dtos = items.Select(item => new PatchInventoryAgentDto(
            item.AgentId,
            item.UpdateKey,
            item.Hostname,
            item.AgentDisplayName,
            item.SiteId,
            item.SiteName,
            item.ClientId,
            item.ClientName,
            item.Status,
            item.ApprovalState,
            item.Severity,
            item.RequiresReboot,
            item.ReportedAt,
            item.LastSeenAt)).ToList();

        return Result<PatchInventoryAgentsDto>.Success(new PatchInventoryAgentsDto(dtos, nextCursor, hasMore));
    }
}

public sealed class GetPatchPendingQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchPendingQuery, Result<PatchPendingListDto>>
{
    public async Task<Result<PatchPendingListDto>> Handle(GetPatchPendingQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var (clientId, siteId) = PatchScopeResolver.Resolve(query.Scope, query.ScopeId);

        var items = await repo.GetPendingAgentsAsync(
            clientId, siteId, query.Cursor, limit + 1, query.Search,
            query.HasGlobalAccess, query.AllowedClientIds, ct);

        var hasMore = items.Count > limit;
        if (hasMore)
            items = items.Take(limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
            nextCursor = Discovery.Core.Helpers.CursorPaginationHelper.EncodeGuidCursor(items[^1].AgentId);

        var dtos = items.Select(item => new PatchPendingAgentDto(
            item.AgentId,
            item.Hostname,
            item.AgentDisplayName,
            item.SiteId,
            item.SiteName,
            item.ClientId,
            item.ClientName,
            item.PendingCount,
            item.CriticalCount,
            item.DeniedCount,
            item.PendingReboot,
            item.ScanStatus,
            item.LastScanAt,
            item.LastSeenAt)).ToList();

        return Result<PatchPendingListDto>.Success(new PatchPendingListDto(dtos, nextCursor, hasMore));
    }
}

public sealed class GetPatchSnapshotQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchSnapshotQuery, Result<PatchScopeSnapshotDto>>
{
    public async Task<Result<PatchScopeSnapshotDto>> Handle(GetPatchSnapshotQuery query, CancellationToken ct)
    {
        var (clientId, siteId) = PatchScopeResolver.Resolve(query.Scope, query.ScopeId);
        var snapshot = await repo.GetScopeSnapshotAsync(
            clientId, siteId, query.HasGlobalAccess, query.AllowedClientIds, ct);

        return Result<PatchScopeSnapshotDto>.Success(ToDto(snapshot));
    }

    internal static PatchScopeSnapshotDto ToDto(PatchScopeSnapshot snapshot)
    {
        var compliance = 0.0;
        if (snapshot.AgentsReporting > 0)
        {
            var compliant = Math.Max(0, snapshot.AgentsReporting - snapshot.AgentsWithPending);
            compliance = Math.Round(100.0 * compliant / snapshot.AgentsReporting, 1);
        }

        return new PatchScopeSnapshotDto(
            snapshot.TotalPending,
            snapshot.CriticalPending,
            snapshot.DeniedPending,
            snapshot.ApprovedPending,
            snapshot.AgentsWithPending,
            snapshot.AgentsTotal,
            snapshot.AgentsReporting,
            snapshot.RebootPendingAgents,
            snapshot.FailedInstallations24h,
            compliance,
            snapshot.LastScanAt);
    }
}

public sealed class GetPatchOverviewQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchOverviewQuery, Result<PatchOverviewDto>>
{
    public async Task<Result<PatchOverviewDto>> Handle(GetPatchOverviewQuery query, CancellationToken ct)
    {
        var (clientId, siteId) = PatchScopeResolver.Resolve(query.Scope, query.ScopeId);

        var snapshot = await repo.GetScopeSnapshotAsync(
            clientId, siteId, query.HasGlobalAccess, query.AllowedClientIds, ct);
        var bySeverity = await repo.GetPendingBySeverityAsync(
            clientId, siteId, query.HasGlobalAccess, query.AllowedClientIds, 10, ct);
        var byClassification = await repo.GetPendingByClassificationAsync(
            clientId, siteId, query.HasGlobalAccess, query.AllowedClientIds, 10, ct);

        var dto = new PatchOverviewDto(
            GetPatchSnapshotQueryHandler.ToDto(snapshot),
            bySeverity.Select(item => new PatchSeverityCountDto(item.Label, item.Count)).ToList(),
            byClassification.Select(item => new PatchSeverityCountDto(item.Label, item.Count)).ToList());

        return Result<PatchOverviewDto>.Success(dto);
    }
}

// ── Aprovações e política ─────────────────────────────────────────────────

public sealed class ListPatchApprovalsQueryHandler(IPatchRepository repo)
    : IRequestHandler<ListPatchApprovalsQuery, Result<IReadOnlyList<PatchApprovalDto>>>
{
    public async Task<Result<IReadOnlyList<PatchApprovalDto>>> Handle(
        ListPatchApprovalsQuery query,
        CancellationToken ct)
    {
        var items = await repo.GetApprovalsAsync(
            query.ScopeType, query.ScopeId, query.Search, Math.Clamp(query.Limit, 1, 500), ct);

        return Result<IReadOnlyList<PatchApprovalDto>>.Success(
            items.Select(PatchDtoMapper.ToDto).ToList());
    }
}

public sealed class ListPatchApprovalAuditQueryHandler(IPatchRepository repo)
    : IRequestHandler<ListPatchApprovalAuditQuery, Result<IReadOnlyList<PatchApprovalAuditDto>>>
{
    public async Task<Result<IReadOnlyList<PatchApprovalAuditDto>>> Handle(
        ListPatchApprovalAuditQuery query,
        CancellationToken ct)
    {
        var items = await repo.GetApprovalAuditAsync(Math.Clamp(query.Limit, 1, 500), ct);
        return Result<IReadOnlyList<PatchApprovalAuditDto>>.Success(
            items.Select(PatchDtoMapper.ToDto).ToList());
    }
}

public sealed class GetPatchPolicyQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchPolicyQuery, Result<PatchPolicyDto>>
{
    public async Task<Result<PatchPolicyDto>> Handle(GetPatchPolicyQuery query, CancellationToken ct)
    {
        var policy = await repo.GetPolicyAsync(query.ScopeType, query.ScopeId, ct);
        if (policy is null)
        {
            // Devolve os defaults conservadores (Monitor, sem auto-install, reboot Never).
            policy = new PatchPolicy
            {
                Id = Guid.Empty,
                ScopeType = query.ScopeType,
                ScopeId = query.ScopeId,
                EnforcementMode = PatchEnforcementMode.Monitor,
                RebootPolicy = PatchRebootPolicy.Never,
                MaxInstallsPerRun = 50
            };
        }

        return Result<PatchPolicyDto>.Success(PatchDtoMapper.ToDto(policy));
    }
}

public sealed class ListPatchPoliciesQueryHandler(IPatchRepository repo)
    : IRequestHandler<ListPatchPoliciesQuery, Result<IReadOnlyList<PatchPolicyDto>>>
{
    public async Task<Result<IReadOnlyList<PatchPolicyDto>>> Handle(
        ListPatchPoliciesQuery query,
        CancellationToken ct)
    {
        var items = await repo.GetPoliciesAsync(ct);
        return Result<IReadOnlyList<PatchPolicyDto>>.Success(
            items.Select(PatchDtoMapper.ToDto).ToList());
    }
}

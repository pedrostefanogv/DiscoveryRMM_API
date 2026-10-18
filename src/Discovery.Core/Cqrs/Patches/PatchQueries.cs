using Discovery.Core.Cqrs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.Patches;

// ── Agente ────────────────────────────────────────────────────────────────

public sealed record GetAgentWindowsUpdatesPageQuery(
    Guid AgentId,
    int Page,
    int PageSize,
    string? Search,
    string? Status,
    string? Severity,
    bool Descending
) : IQuery<Result<AgentWindowsUpdatePageDto>>;

public sealed record GetAgentWindowsUpdatesSnapshotQuery(Guid AgentId)
    : IQuery<Result<AgentWindowsUpdateSnapshotDto>>;

// ── Frota ─────────────────────────────────────────────────────────────────

public sealed record GetPatchInventoryQuery(
    PatchScope Scope,
    Guid? ScopeId,
    string? Cursor,
    int Limit,
    string? Search,
    string? Severity,
    string? Status,
    bool Descending,
    bool HasGlobalAccess,
    IReadOnlyList<Guid> AllowedClientIds
) : IQuery<Result<PatchInventoryListDto>>;

public sealed record GetPatchInventoryAgentsQuery(
    string GroupKey,
    PatchScope Scope,
    Guid? ScopeId,
    string? Cursor,
    int Limit,
    string? Status,
    bool HasGlobalAccess,
    IReadOnlyList<Guid> AllowedClientIds
) : IQuery<Result<PatchInventoryAgentsDto>>;

public sealed record GetPatchPendingQuery(
    PatchScope Scope,
    Guid? ScopeId,
    string? Cursor,
    int Limit,
    string? Search,
    bool HasGlobalAccess,
    IReadOnlyList<Guid> AllowedClientIds
) : IQuery<Result<PatchPendingListDto>>;

public sealed record GetPatchSnapshotQuery(
    PatchScope Scope,
    Guid? ScopeId,
    bool HasGlobalAccess,
    IReadOnlyList<Guid> AllowedClientIds
) : IQuery<Result<PatchScopeSnapshotDto>>;

public sealed record GetPatchOverviewQuery(
    PatchScope Scope,
    Guid? ScopeId,
    bool HasGlobalAccess,
    IReadOnlyList<Guid> AllowedClientIds
) : IQuery<Result<PatchOverviewDto>>;

// ── Aprovações e política ─────────────────────────────────────────────────

public sealed record ListPatchApprovalsQuery(
    PatchApprovalScopeType? ScopeType,
    Guid? ScopeId,
    string? Search,
    int Limit
) : IQuery<Result<IReadOnlyList<PatchApprovalDto>>>;

public sealed record ListPatchApprovalAuditQuery(int Limit)
    : IQuery<Result<IReadOnlyList<PatchApprovalAuditDto>>>;

public sealed record GetPatchPolicyQuery(
    PatchApprovalScopeType ScopeType,
    Guid? ScopeId
) : IQuery<Result<PatchPolicyDto>>;

public sealed record ListPatchPoliciesQuery : IQuery<Result<IReadOnlyList<PatchPolicyDto>>>;

using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Acesso a dados do gerenciador de patches: inventário de Windows Update,
/// aprovações, políticas e auditoria de enforcement.
/// </summary>
public interface IPatchRepository
{
    // ── Agente ──────────────────────────────────────────────────────────────
    Task<AgentWindowsUpdatePageResult> GetAgentUpdatesPageAsync(
        Guid agentId,
        int page,
        int pageSize,
        string? search,
        PatchUpdateStatus? status,
        string? severity,
        bool descending,
        CancellationToken ct = default);

    Task<AgentWindowsUpdateSnapshot?> GetSnapshotAsync(Guid agentId, CancellationToken ct = default);

    Task<AgentWindowsUpdate?> GetAgentUpdateAsync(Guid agentId, string updateKey, CancellationToken ct = default);

    Task<IReadOnlyList<AgentWindowsUpdate>> GetPendingForAgentAsync(Guid agentId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentWindowsUpdate>> GetPendingForAgentsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default);

    Task ReplaceInventoryAsync(
        Guid agentId,
        DateTime collectedAt,
        PatchScanMeta scan,
        PatchPendingRebootMeta? pendingReboot,
        IReadOnlyList<AgentWindowsUpdateEntry> updates,
        CancellationToken ct = default);

    /// <summary>Atualiza somente os metadados do scan (status/erro/duração).</summary>
    Task UpdateScanStatusAsync(
        Guid agentId,
        PatchScanMeta scan,
        PatchPendingRebootMeta? pendingReboot,
        CancellationToken ct = default);

    Task UpdateAgentPendingRebootAsync(
        Guid agentId,
        bool pending,
        IReadOnlyList<string> reasons,
        DateTime checkedAt,
        CancellationToken ct = default);

    /// <summary>Marca instalações sem resultado há mais de <paramref name="maxAge"/> como Failed.</summary>
    Task<int> ExpireStaleInstallingAsync(TimeSpan maxAge, CancellationToken ct = default);

    /// <summary>Remove linhas de inventário ausentes há mais de <paramref name="maxAge"/> (retenção).</summary>
    Task<int> PurgeStaleInventoryAsync(TimeSpan maxAge, CancellationToken ct = default);

    Task UpdateInstallStateAsync(
        Guid agentId,
        IReadOnlyCollection<string> updateKeys,
        PatchUpdateStatus status,
        int? resultCode,
        string? error,
        CancellationToken ct = default);

    // ── Frota ───────────────────────────────────────────────────────────────
    Task<IReadOnlyList<PatchInventoryGroup>> GetInventoryGroupsAsync(
        Guid? clientId,
        Guid? siteId,
        string? cursor,
        int limit,
        string? search,
        string? severity,
        string? status,
        bool descending,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        CancellationToken ct = default);

    Task<IReadOnlyList<PatchInventoryAgentRow>> GetInventoryAgentsAsync(
        string groupKey,
        Guid? clientId,
        Guid? siteId,
        string? cursor,
        int limit,
        PatchUpdateStatus? status,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        CancellationToken ct = default);

    Task<IReadOnlyList<PatchPendingAgentRow>> GetPendingAgentsAsync(
        Guid? clientId,
        Guid? siteId,
        string? cursor,
        int limit,
        string? search,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        CancellationToken ct = default);

    Task<PatchScopeSnapshot> GetScopeSnapshotAsync(
        Guid? clientId,
        Guid? siteId,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        CancellationToken ct = default);

    Task<IReadOnlyList<PatchSeverityCount>> GetPendingBySeverityAsync(
        Guid? clientId,
        Guid? siteId,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<PatchSeverityCount>> GetPendingByClassificationAsync(
        Guid? clientId,
        Guid? siteId,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAgentIdsInScopeAsync(
        Guid? clientId,
        Guid? siteId,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        int limit,
        CancellationToken ct = default);


    // ── Aprovações ──────────────────────────────────────────────────────────
    Task<IReadOnlyList<PatchApproval>> GetApprovalsAsync(
        PatchApprovalScopeType? scopeType,
        Guid? scopeId,
        string? search,
        int limit,
        CancellationToken ct = default);

    Task<PatchApproval?> GetApprovalAsync(
        PatchApprovalScopeType scopeType,
        Guid? scopeId,
        string updateKey,
        CancellationToken ct = default);

    Task<PatchApproval?> GetApprovalByIdAsync(Guid id, CancellationToken ct = default);

    Task AddApprovalAuditAsync(PatchApprovalAudit audit, CancellationToken ct = default);

    Task<PatchApproval> UpsertApprovalAsync(
        PatchApproval approval,
        PatchApprovalAudit audit,
        CancellationToken ct = default);

    Task<bool> DeleteApprovalAsync(
        Guid id,
        string? changedBy,
        string? ipAddress,
        CancellationToken ct = default);

    Task<IReadOnlyList<PatchApprovalAudit>> GetApprovalAuditAsync(
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<PatchApproval>> GetApplicableApprovalsForAgentAsync(
        Guid agentId,
        Guid clientId,
        Guid siteId,
        CancellationToken ct = default);

    // ── Políticas ───────────────────────────────────────────────────────────
    Task<PatchPolicy?> GetPolicyAsync(
        PatchApprovalScopeType scopeType,
        Guid? scopeId,
        CancellationToken ct = default);

    Task<IReadOnlyList<PatchPolicy>> GetApplicablePoliciesForAgentAsync(
        Guid clientId,
        Guid siteId,
        CancellationToken ct = default);

    Task<PatchPolicy> UpsertPolicyAsync(PatchPolicy policy, CancellationToken ct = default);

    Task<IReadOnlyList<PatchPolicy>> GetPoliciesAsync(CancellationToken ct = default);

    // ── Enforcement no SO ───────────────────────────────────────────────────
    Task<PatchOsState> SaveOsStateAsync(PatchOsState state, CancellationToken ct = default);

    Task<PatchOsState?> GetLatestOsStateAsync(Guid agentId, CancellationToken ct = default);

    Task MarkOsStateRevertedAsync(Guid stateId, string? revertedBy, CancellationToken ct = default);

    /// <summary>Grava o backup devolvido pelo agente no estado de enforcement mais recente.</summary>
    Task UpdateOsStateBackupAsync(Guid agentId, string previousJson, CancellationToken ct = default);
}

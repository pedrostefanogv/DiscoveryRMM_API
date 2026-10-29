using Discovery.Core.Cqrs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.Patches;

// ── Agente ────────────────────────────────────────────────────────────────

public sealed record ScanAgentWindowsUpdatesCommand(Guid AgentId, string? CorrelationId = null)
    : ICommand<Result<PatchDispatchDto>>;

public sealed record InstallAgentWindowsUpdateCommand(
    Guid AgentId,
    string UpdateKey,
    bool Force,
    string? PostAction,
    string? CorrelationId = null,
    string? RequestedBy = null,
    string? IpAddress = null,
    string? Reason = null
) : ICommand<Result<Guid>>;

public sealed record InstallAgentApprovedWindowsUpdatesCommand(
    Guid AgentId,
    string? PostAction,
    bool Confirm,
    string? CorrelationId = null
) : ICommand<Result<PatchDispatchDto>>;

public sealed record SetAgentWindowsUpdateApprovalCommand(
    Guid AgentId,
    string UpdateKey,
    PatchApprovalScopeType Scope,
    PatchApprovalActionType Action,
    string? Reason,
    string? ChangedBy,
    string? IpAddress,
    Guid? TargetScopeId = null
) : ICommand<Result<PatchApprovalDto>>;

public sealed record ApproveAgentPendingWindowsUpdatesCommand(
    Guid AgentId,
    PatchApprovalActionType Action,
    PatchApprovalScopeType Scope,
    string? Reason,
    string? ChangedBy,
    string? IpAddress
) : ICommand<Result<PatchDispatchDto>>;

public sealed record ApplyAgentPatchOsPolicyCommand(
    Guid AgentId,
    bool HideDenied,
    string? RequestedBy,
    string? IpAddress
) : ICommand<Result<PatchDispatchDto>>;

public sealed record RollbackAgentPatchOsPolicyCommand(
    Guid AgentId,
    string? RequestedBy
) : ICommand<Result<PatchDispatchDto>>;

public sealed record BulkAgentWindowsUpdateCommand(
    Guid AgentId,
    string Action,
    IReadOnlyList<string> UpdateKeys,
    PatchApprovalScopeType? Scope,
    string? Reason,
    string? ChangedBy,
    string? IpAddress
) : ICommand<Result<PatchDispatchDto>>;

// ── Aprovações e política ─────────────────────────────────────────────────

public sealed record CreatePatchApprovalCommand(
    PatchApprovalScopeType ScopeType,
    Guid? ScopeId,
    string UpdateKey,
    PatchApprovalActionType Action,
    string? Reason,
    string? ChangedBy,
    string? IpAddress
) : ICommand<Result<PatchApprovalDto>>;

public sealed record DeletePatchApprovalCommand(
    Guid Id,
    string? ChangedBy,
    string? IpAddress
) : ICommand<Result<VoidResult>>;

public sealed record UpdatePatchPolicyCommand(
    PatchApprovalScopeType ScopeType,
    Guid? ScopeId,
    PatchEnforcementMode EnforcementMode,
    IReadOnlyList<string> AutoApproveClassifications,
    IReadOnlyList<string> AutoApproveSeverities,
    bool AutoInstallEnabled,
    string? MaintenanceWindowJson,
    PatchRebootPolicy RebootPolicy,
    bool RebootAllowed,
    int MaxInstallsPerRun,
    bool HideDeniedUpdates,
    string? UpdatedBy,
    // WU-04/D09: dias de retenção por idade de publicação (0 = sem deferral).
    int DeferralDays = 0,
    // WU-05/D03: deadline de instalação em dias desde a publicação (0 = sem).
    int DeadlineDays = 0,
    // WU-19/WU-20/D22: origens habilitadas e consentimento de terceiros.
    IReadOnlyList<string>? Sources = null,
    bool AllowThirdPartyApps = false
) : ICommand<Result<PatchPolicyDto>>;

// ── Frota (ações em lote) ─────────────────────────────────────────────────

public sealed record ScanPatchScopeCommand(
    PatchScope Scope,
    Guid? ScopeId,
    IReadOnlyList<Guid>? AgentIds,
    bool HasGlobalAccess,
    IReadOnlyList<Guid> AllowedClientIds,
    string? CorrelationId = null
) : ICommand<Result<PatchDispatchDto>>;

public sealed record InstallPatchScopeCommand(
    PatchScope Scope,
    Guid? ScopeId,
    IReadOnlyList<string> UpdateKeys,
    bool Force,
    bool Confirm,
    string? PostAction,
    bool HasGlobalAccess,
    IReadOnlyList<Guid> AllowedClientIds,
    string? CorrelationId = null,
    string? Reason = null,
    string? RequestedBy = null,
    // WU-06/D07: instalação manual respeita a janela; o operador pode
    // sobrepor com motivo obrigatório (auditado no relatório).
    bool OverrideWindow = false
) : ICommand<Result<PatchDispatchDto>>;

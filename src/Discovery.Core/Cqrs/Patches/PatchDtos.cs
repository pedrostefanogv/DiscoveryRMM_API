using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.Patches;

/// <summary>Escopo usado pelas queries de frota de patches.</summary>
public enum PatchScope
{
    Global = 0,
    Client = 1,
    Site = 2
}

public sealed record AgentWindowsUpdateDto(
    Guid Id,
    string UpdateKey,
    string GroupKey,
    string? KbId,
    IReadOnlyList<string> KbIds,
    string Title,
    string? Description,
    string? Classification,
    string? Severity,
    bool IsPending,
    bool IsInstalled,
    bool IsHidden,
    bool RequiresReboot,
    long? MaxDownloadSize,
    string? SupportUrl,
    DateTime? PublishedAt,
    DateTime? InstalledAt,
    DateTime ReportedAt,
    PatchApprovalState ApprovalState,
    string? ApprovalSource,
    PatchUpdateStatus Status,
    int? InstallResultCode,
    string? InstallError,
    string Source)
{
    /// <summary>
    /// Baixada pelo Windows Update mas ainda não instalada (persistida em
    /// is_downloaded). Propriedade init — não muda o contrato posicional.
    /// </summary>
    public bool IsDownloaded { get; init; }

    /// <summary>Marcada como obrigatória pela busca WUA (is_mandatory).</summary>
    public bool IsMandatory { get; init; }
}

public sealed record AgentWindowsUpdatePageDto(
    IReadOnlyList<AgentWindowsUpdateDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int PendingCount,
    int InstalledCount);

/// <summary>
/// Snapshot da aba "Atualizações" do agente.
///
/// Parâmetros posicionais até <c>RebootCheckedAt</c>; os campos da sonda de
/// estado (<c>ServiceState</c>…) são propriedades opcionais no fim do record —
/// agentes/site novos leem os dois jeitos.
/// </summary>
public sealed record AgentWindowsUpdateSnapshotDto(
    Guid AgentId,
    int PendingCount,
    int InstalledCount,
    int DeniedCount,
    int ApprovedCount,
    string ScanStatus,
    string? ScanError,
    int? ScanDurationMs,
    DateTime? LastCollectedAt,
    bool PendingReboot,
    IReadOnlyList<string> PendingRebootReasons,
    DateTime? RebootCheckedAt)
{
    /// <summary>running | stopped | disabled | unknown (null = agente antigo).</summary>
    public string? ServiceState { get; init; }
    /// <summary>O Windows Update Online respondeu no último scan.</summary>
    public bool? WuOnline { get; init; }
    /// <summary>WSUS/GPO gerenciando o Windows Update da máquina.</summary>
    public bool? PolicyManaged { get; init; }
    /// <summary>Results\Install\LastSuccessTime.</summary>
    public DateTime? LastInstallSuccessAt { get; init; }
    /// <summary>Download de update em andamento (sonda leve).</summary>
    public bool? DownloadInProgress { get; init; }
    /// <summary>Assinatura da sonda no último scan (mudou = novo scan).</summary>
    public string? StateHash { get; init; }
    /// <summary>Quando a assinatura da sonda mudou pela última vez.</summary>
    public DateTime? StateChangedAt { get; init; }
    /// <summary>Código estável do erro do scan (ex.: service_disabled).</summary>
    public string? ScanErrorCode { get; init; }
    /// <summary>periodic | change | command | install (null = agente antigo).</summary>
    public string? ScanTrigger { get; init; }
    /// <summary>wua | qfe (null = agente antigo).</summary>
    public string? ScanSource { get; init; }
    /// <summary>Quantidade de resultados descartados pelo scan.</summary>
    public int SkippedCount { get; init; }
    /// <summary>Motivo → quantidade dos descartes: driver, no_identity, unreadable.</summary>
    public IReadOnlyDictionary<string, int>? SkippedReasons { get; init; }
    /// <summary>Servidor WSUS configurado na máquina (vazio = não configurado).</summary>
    public string? WsusServer { get; init; }
    /// <summary>AUOptions da política de Windows Update (0–5).</summary>
    public int? AuOptions { get; init; }
    /// <summary>NoAutoUpdate=1 (atualização automática desligada por política).</summary>
    public bool? NoAutoUpdate { get; init; }
    /// <summary>Deferral de quality/feature updates em dias.</summary>
    public int? DeferralDays { get; init; }
    /// <summary>Início da janela de horas ativas (0–23).</summary>
    public int? ActiveHoursStart { get; init; }
    /// <summary>Fim da janela de horas ativas (0–23).</summary>
    public int? ActiveHoursEnd { get; init; }
    /// <summary>KBs instaladas contadas no registro (CBS) pela sonda.</summary>
    public int? QfeInstalledCount { get; init; }
    /// <summary>Percentual do download em andamento (0–100).</summary>
    public int? DownloadPercent { get; init; }

    /// <summary>
    /// Bloco <c>probe</c> consumido pela aba "Atualizações" do console.
    ///
    /// O site tipa <c>snapshot.probe</c> (service, serviceDisabled, wsus,
    /// policyManaged, lastInstallResult, lastInstallSuccessAt,
    /// downloadInProgress, downloadPercent, probedAt). Sem este objeto o painel
    /// simplesmente não mostra a linha de diagnóstico — por isso ele deriva dos
    /// campos tipados acima, com nomes idênticos ao contrato do site.
    /// </summary>
    public AgentWindowsUpdateProbeDto? Probe
    {
        get
        {
            if (ServiceState is null && WuOnline is null && PolicyManaged is null &&
                LastInstallSuccessAt is null && DownloadInProgress is null &&
                WsusServer is null && AuOptions is null && NoAutoUpdate is null &&
                DeferralDays is null && ActiveHoursStart is null && ActiveHoursEnd is null &&
                QfeInstalledCount is null && DownloadPercent is null)
            {
                return null;
            }

            return new AgentWindowsUpdateProbeDto(
                ServiceState,
                string.Equals(ServiceState, "disabled", StringComparison.OrdinalIgnoreCase),
                // WSUS quando há política OU servidor configurado (string vazia = não configurado).
                PolicyManaged is true || !string.IsNullOrWhiteSpace(WsusServer),
                PolicyManaged,
                null,
                LastInstallSuccessAt,
                DownloadInProgress,
                DownloadPercent,
                StateChangedAt ?? LastCollectedAt)
            {
                WsusServer = WsusServer,
                AuOptions = AuOptions,
                NoAutoUpdate = NoAutoUpdate,
                DeferralDays = DeferralDays,
                ActiveHoursStart = ActiveHoursStart,
                ActiveHoursEnd = ActiveHoursEnd,
                QfeInstalledCount = QfeInstalledCount
            };
        }
    }
}

/// <summary>Sonda de estado exposta ao console (compatível com agentes antigos: tudo null).</summary>
public sealed record AgentWindowsUpdateProbeDto(
    string? Service,
    bool? ServiceDisabled,
    bool? Wsus,
    bool? PolicyManaged,
    int? LastInstallResult,
    DateTime? LastInstallSuccessAt,
    bool? DownloadInProgress,
    int? DownloadPercent,
    DateTime? ProbedAt)
{
    /// <summary>Nome do servidor WSUS configurado na máquina.</summary>
    public string? WsusServer { get; init; }
    /// <summary>AUOptions da política (0–5).</summary>
    public int? AuOptions { get; init; }
    /// <summary>NoAutoUpdate=1 (atualização automática desligada por política).</summary>
    public bool? NoAutoUpdate { get; init; }
    /// <summary>Deferral de quality/feature updates em dias.</summary>
    public int? DeferralDays { get; init; }
    /// <summary>Início da janela de horas ativas (0–23).</summary>
    public int? ActiveHoursStart { get; init; }
    /// <summary>Fim da janela de horas ativas (0–23).</summary>
    public int? ActiveHoursEnd { get; init; }
    /// <summary>KBs instaladas contadas no registro (CBS).</summary>
    public int? QfeInstalledCount { get; init; }
}

public sealed record PatchInventoryGroupDto(
    string GroupKey,
    IReadOnlyList<string> KbIds,
    string Title,
    string? Classification,
    string? Severity,
    DateTime? PublishedAt,
    int PendingAgents,
    int InstalledAgents,
    int DeniedAgents,
    int ReportingAgents,
    DateTime? FirstSeenAt,
    DateTime? LastSeenAt);

public sealed record PatchInventoryListDto(
    IReadOnlyList<PatchInventoryGroupDto> Items,
    string? NextCursor,
    bool HasMore);

public sealed record PatchInventoryAgentDto(
    Guid AgentId,
    string UpdateKey,
    string Hostname,
    string? AgentDisplayName,
    Guid SiteId,
    string SiteName,
    Guid ClientId,
    string ClientName,
    PatchUpdateStatus Status,
    PatchApprovalState ApprovalState,
    string? Severity,
    bool RequiresReboot,
    DateTime ReportedAt,
    DateTime LastSeenAt);

public sealed record PatchInventoryAgentsDto(
    IReadOnlyList<PatchInventoryAgentDto> Items,
    string? NextCursor,
    bool HasMore);

public sealed record PatchPendingAgentDto(
    Guid AgentId,
    string Hostname,
    string? AgentDisplayName,
    Guid SiteId,
    string SiteName,
    Guid ClientId,
    string ClientName,
    int PendingCount,
    int CriticalCount,
    int DeniedCount,
    bool PendingReboot,
    string? ScanStatus,
    DateTime? LastScanAt,
    DateTime? LastSeenAt);

public sealed record PatchPendingListDto(
    IReadOnlyList<PatchPendingAgentDto> Items,
    string? NextCursor,
    bool HasMore);

public sealed record PatchScopeSnapshotDto(
    int TotalPending,
    int CriticalPending,
    int DeniedPending,
    int ApprovedPending,
    int AgentsWithPending,
    int AgentsTotal,
    int AgentsReporting,
    int RebootPendingAgents,
    int FailedInstallations24h,
    double CompliancePercent,
    DateTime? LastScanAt);

public sealed record PatchSeverityCountDto(string Label, int Count);

public sealed record PatchOverviewDto(
    PatchScopeSnapshotDto Snapshot,
    IReadOnlyList<PatchSeverityCountDto> BySeverity,
    IReadOnlyList<PatchSeverityCountDto> ByClassification);

public sealed record PatchApprovalDto(
    Guid Id,
    PatchApprovalScopeType ScopeType,
    Guid? ScopeId,
    Guid? ClientId,
    Guid? SiteId,
    Guid? AgentId,
    string UpdateKey,
    string? KbId,
    PatchApprovalActionType Action,
    string? Reason,
    string? CreatedBy,
    string? UpdatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record PatchApprovalAuditDto(
    Guid Id,
    Guid? RuleId,
    PatchApprovalAuditChangeType ChangeType,
    PatchApprovalScopeType ScopeType,
    Guid? ScopeId,
    string UpdateKey,
    string? KbId,
    PatchApprovalActionType? OldAction,
    PatchApprovalActionType? NewAction,
    string? Reason,
    string? ChangedBy,
    string? IpAddress,
    DateTime ChangedAt);

public sealed record PatchPolicyDto(
    Guid Id,
    PatchApprovalScopeType ScopeType,
    Guid? ScopeId,
    Guid? ClientId,
    Guid? SiteId,
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
    DateTime UpdatedAt);

public sealed record PatchDispatchDto(
    int Dispatched,
    int Skipped,
    IReadOnlyList<Guid> CommandIds,
    string Message);

/// <summary>Mapeadores de entidade para DTO (usados por handlers e controllers).</summary>
public static class PatchDtoMapper
{
    public static AgentWindowsUpdateDto ToDto(AgentWindowsUpdate update)
    {
        var kbIds = PatchJson.ParseStringArray(update.KbIds);
        return new AgentWindowsUpdateDto(
            update.Id,
            update.UpdateKey,
            update.GroupKey,
            update.GroupKey.StartsWith("kb:", StringComparison.OrdinalIgnoreCase)
                ? update.GroupKey[3..]
                : (kbIds.Count > 0 ? kbIds[0] : null),
            kbIds,
            update.Title,
            update.Description,
            update.Classification,
            update.Severity,
            update.IsPending,
            update.IsInstalled,
            update.IsHidden,
            update.RequiresReboot,
            update.MaxDownloadSize,
            update.SupportUrl,
            update.PublishedAt,
            update.InstalledAt,
            update.ReportedAt,
            update.ApprovalState,
            update.ApprovalSource,
            update.Status,
            update.InstallResultCode,
            update.InstallError,
            update.Source ?? "wua")
        {
            IsDownloaded = update.IsDownloaded,
            IsMandatory = update.IsMandatory
        };
    }

    public static AgentWindowsUpdateSnapshotDto ToSnapshotDto(
        Guid agentId,
        AgentWindowsUpdateSnapshot? snapshot,
        int pendingCount,
        int installedCount,
        int deniedCount,
        int approvedCount)
    {
        return new AgentWindowsUpdateSnapshotDto(
            agentId,
            snapshot?.PendingCount ?? pendingCount,
            snapshot?.InstalledCount ?? installedCount,
            snapshot?.DeniedCount ?? deniedCount,
            approvedCount,
            snapshot?.ScanStatus ?? "unknown",
            snapshot?.ScanError,
            snapshot?.ScanDurationMs,
            snapshot?.CollectedAt,
            snapshot?.PendingReboot ?? false,
            PatchJson.ParseStringArray(snapshot?.PendingRebootReasons),
            snapshot?.RebootCheckedAt)
        {
            // Sonda opcional: null quando o agente nunca enviou o bloco "state"
            // (compatibilidade total com agentes antigos).
            ServiceState = snapshot?.ServiceState,
            WuOnline = snapshot?.WuOnline,
            PolicyManaged = snapshot?.PolicyManaged,
            LastInstallSuccessAt = snapshot?.LastInstallSuccessAt,
            DownloadInProgress = snapshot?.DownloadInProgress,
            StateHash = snapshot?.StateHash,
            StateChangedAt = snapshot?.StateChangedAt,
            // Diagnóstico do scan (M239) — null/0 quando o agente é antigo.
            ScanErrorCode = snapshot?.ScanErrorCode,
            ScanTrigger = snapshot?.ScanTrigger,
            ScanSource = snapshot?.ScanSource,
            SkippedCount = snapshot?.SkippedCount ?? 0,
            SkippedReasons = PatchJson.ParseIntDictionary(snapshot?.SkippedReasons),
            // Sonda estendida (M239): WSUS/AU/deferral/horas ativas/CBS/download.
            WsusServer = snapshot?.WsusServer,
            AuOptions = snapshot?.AuOptions,
            NoAutoUpdate = snapshot?.NoAutoUpdate,
            DeferralDays = snapshot?.DeferralDays,
            ActiveHoursStart = snapshot?.ActiveHoursStart,
            ActiveHoursEnd = snapshot?.ActiveHoursEnd,
            QfeInstalledCount = snapshot?.QfeInstalledCount,
            DownloadPercent = snapshot?.DownloadPercent
        };
    }

    public static PatchApprovalDto ToDto(PatchApproval approval) => new(
        approval.Id,
        approval.ScopeType,
        approval.ScopeId,
        approval.ClientId,
        approval.SiteId,
        approval.AgentId,
        approval.UpdateKey,
        approval.KbId,
        approval.Action,
        approval.Reason,
        approval.CreatedBy,
        approval.UpdatedBy,
        approval.CreatedAt,
        approval.UpdatedAt);

    public static PatchApprovalAuditDto ToDto(PatchApprovalAudit audit) => new(
        audit.Id,
        audit.RuleId,
        audit.ChangeType,
        audit.ScopeType,
        audit.ScopeId,
        audit.UpdateKey,
        audit.KbId,
        audit.OldAction,
        audit.NewAction,
        audit.Reason,
        audit.ChangedBy,
        audit.IpAddress,
        audit.ChangedAt);

    public static PatchPolicyDto ToDto(PatchPolicy policy) => new(
        policy.Id,
        policy.ScopeType,
        policy.ScopeId,
        policy.ClientId,
        policy.SiteId,
        policy.EnforcementMode,
        PatchJson.ParseStringArray(policy.AutoApproveClassifications),
        PatchJson.ParseStringArray(policy.AutoApproveSeverities),
        policy.AutoInstallEnabled,
        policy.MaintenanceWindowJson,
        policy.RebootPolicy,
        policy.RebootAllowed,
        policy.MaxInstallsPerRun,
        policy.HideDeniedUpdates,
        policy.UpdatedBy,
        policy.UpdatedAt);
}

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
        string? source = null,
        CancellationToken ct = default);

    Task<AgentWindowsUpdateSnapshot?> GetSnapshotAsync(Guid agentId, CancellationToken ct = default);

    Task<AgentWindowsUpdate?> GetAgentUpdateAsync(Guid agentId, string updateKey, CancellationToken ct = default);

    Task<IReadOnlyList<AgentWindowsUpdate>> GetPendingForAgentAsync(Guid agentId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentWindowsUpdate>> GetPendingForAgentsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default);

    /// <summary>
    /// Substitui o inventário do agente pelo conteúdo do upload.
    ///
    /// Regras de preservação (upload parcial/vazio nunca destrói dado bom):
    /// • lista vazia → só metadados (status/sonda) são atualizados;
    /// • <c>scan.status = partial</c> sem linha instalada → mantém as instaladas;
    /// • <c>scan.status = partial</c> sem linha pendente → mantém as pendentes
    ///   (nunca marca Superseded por silêncio de uma fonte que falhou);
    /// • contador que a fonte parcial não viu não é zerado;
    /// • <paramref name="serviceState"/> nulo não apaga o estado conhecido.
    /// </summary>
    Task ReplaceInventoryAsync(
        Guid agentId,
        DateTime collectedAt,
        PatchScanMeta scan,
        PatchPendingRebootMeta? pendingReboot,
        IReadOnlyList<AgentWindowsUpdateEntry> updates,
        PatchServiceStateMeta? serviceState = null,
        CancellationToken ct = default);

    /// <summary>
    /// Aplica um upload DELTA (WU-13): mescla os itens alterados com o inventário
    /// atual do agente, remove as chaves informadas e delega para
    /// <see cref="ReplaceInventoryAsync"/> — preservando todas as guardas de scan
    /// parcial/ilegível. Um delta que esvazia o inventário é ignorado (mesma
    /// semântica de "upload vazio não prova remoção").
    /// </summary>
    Task ApplyInventoryDeltaAsync(
        Guid agentId,
        DateTime collectedAt,
        PatchScanMeta scan,
        PatchPendingRebootMeta? pendingReboot,
        IReadOnlyList<AgentWindowsUpdateEntry> changedEntries,
        IReadOnlyList<string>? removedKeys,
        PatchServiceStateMeta? serviceState = null,
        CancellationToken ct = default);

    /// <summary>Atualiza somente os metadados do scan (status/erro/duração).</summary>
    Task UpdateScanStatusAsync(
        Guid agentId,
        PatchScanMeta scan,
        PatchPendingRebootMeta? pendingReboot,
        CancellationToken ct = default);

    /// <summary>Atualiza metadados do scan + sonda de estado do serviço.</summary>
    Task UpdateScanStatusAsync(
        Guid agentId,
        PatchScanMeta scan,
        PatchPendingRebootMeta? pendingReboot,
        PatchServiceStateMeta? serviceState,
        CancellationToken ct = default);

    /// <summary>
    /// Grava só a sonda de estado do serviço Windows Update (bloco "state").
    /// Campo ausente no payload não zera o valor conhecido.
    /// </summary>
    Task UpdateServiceStateAsync(
        Guid agentId,
        PatchServiceStateMeta state,
        CancellationToken ct = default);

    Task UpdateAgentPendingRebootAsync(
        Guid agentId,
        bool pending,
        IReadOnlyList<string> reasons,
        DateTime checkedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Agentes com reinício pendente (badge ligado), para o job que consome a
    /// política de reboot. Limitado para não varrer a frota inteira por ciclo.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetAgentsWithPendingRebootAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Persiste a ação decidida pela política de reboot (decisão D02: nunca
    /// reiniciar automaticamente; a política gera sinal operacional).
    /// </summary>
    Task UpdateRebootActionAsync(
        Guid agentId,
        PatchRebootAction action,
        string reason,
        DateTime decidedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Chaves de apps de terceiros presentes no inventário (WU-21), para o job de
    /// ingestão de manifestos. Devolve pares "source:appId" distintos.
    /// </summary>
    Task<IReadOnlyList<string>> GetThirdPartyAppKeysAsync(int limit, CancellationToken ct = default);

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

    /// <summary>
    /// Registra falha de instalação com classificação e backoff (WU-08). Incrementa
    /// <c>attempt_count</c>, grava <c>failure_class</c> e agenda a próxima tentativa
    /// (somente quando a classe permite retry).
    /// </summary>
    Task RecordInstallFailureAsync(
        Guid agentId,
        IReadOnlyCollection<string> updateKeys,
        string failureClass,
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

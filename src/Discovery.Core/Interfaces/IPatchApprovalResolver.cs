using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Recalcula o estado efetivo de aprovação (denormalizado em
/// <c>agent_windows_updates.approval_state</c>) a partir das regras explícitas
/// (Global → Agent) e das políticas de auto-aprovação.
/// </summary>
public interface IPatchApprovalResolver
{
    /// <summary>Recalcula todas as linhas de inventário de um agente. Retorna quantas mudaram.</summary>
    Task<int> RecomputeForAgentAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Recalcula as linhas de todos os agentes que têm a chave de grupo.</summary>
    Task<int> RecomputeForGroupKeyAsync(string groupKey, CancellationToken ct = default);

    /// <summary>Recalcula as linhas afetadas por uma mudança de política/regra no escopo.</summary>
    Task<int> RecomputeForScopeAsync(
        PatchApprovalScopeType scopeType,
        Guid? scopeId,
        CancellationToken ct = default);

    /// <summary>Recalcula snapshot (contadores) do agente.</summary>
    Task RefreshSnapshotCountsAsync(Guid agentId, CancellationToken ct = default);
}

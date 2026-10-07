using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Repositório para consulta e administração de políticas MCP tool por escopo.
/// Escopos: global (tudo NULL), client (clientId), site (siteId), agent (agentId).
/// </summary>
public interface IMcpToolPolicyRepository
{
    /// <summary>
    /// Retorna todas as políticas aplicáveis ao escopo (match exato ou global).
    /// Ordem de prioridade: agent > site > client > global (NULL em todos).
    /// Uma política com Locked=true interrompe a sobrescrita por escopos mais
    /// específicos (bloqueio de herança).
    /// </summary>
    Task<IReadOnlyList<McpToolPolicy>> GetEffectivePoliciesAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        CancellationToken ct = default);

    /// <summary>
    /// Retorna a política exata para uma tool no escopo especificado, ou null.
    /// </summary>
    Task<McpToolPolicy?> GetPolicyAsync(
        string toolName,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        CancellationToken ct = default);

    /// <summary>
    /// Lista TODAS as linhas de política que pertencem ao escopo exato informado
    /// (sem mesclar herança). Usado pela tela de settings para mostrar o que está
    /// sobrescrito neste nível.
    /// </summary>
    Task<IReadOnlyList<McpToolPolicy>> GetScopePoliciesAsync(
        Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default);

    /// <summary>
    /// Cria ou atualiza a política exata do escopo (global/client/site/agent).
    /// </summary>
    Task<McpToolPolicy> UpsertAsync(McpToolPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Remove a sobrescrita do escopo (a tool volta a herdar do nível acima).
    /// Retorna false quando não havia linha no escopo.
    /// </summary>
    Task<bool> DeleteScopePolicyAsync(
        string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default);

    /// <summary>
    /// Garante que exista uma policy global (is_enabled=true) para cada tool
    /// informada — usado no auto-registro das tools do agente. Não altera
    /// policies existentes (inclusive desabilitadas pelo operador).
    /// </summary>
    Task EnsureGlobalPoliciesAsync(
        string source, IEnumerable<string> toolNames, CancellationToken ct = default);

    /// <summary>
    /// Conta quantas sobrescritas existem em escopos MAIS específicos que o
    /// informado para a tool — usado para avisar o impacto de bloquear/desabilitar
    /// no nível atual.
    /// </summary>
    Task<int> CountLowerScopeOverridesAsync(
        string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default);

    /// <summary>
    /// Versão em LOTE de <see cref="CountLowerScopeOverridesAsync"/>: devolve a
    /// contagem por tool em UMA passada (evita N+1 consultas ao montar o
    /// catálogo, que tem dezenas de ferramentas).
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetLowerScopeOverrideCountsAsync(
        IReadOnlyCollection<string> toolNames, Guid? clientId, Guid? siteId, Guid? agentId,
        CancellationToken ct = default);

    /// <summary>
    /// Retorna a política de um ANCESTRAL (nível mais genérico) quando ela está
    /// Locked — nesse caso o escopo atual não pode sobrescrevê-la. Null quando
    /// não há ancestral bloqueando.
    /// </summary>
    Task<McpToolPolicy?> GetAncestorLockedPolicyAsync(
        string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default);
}

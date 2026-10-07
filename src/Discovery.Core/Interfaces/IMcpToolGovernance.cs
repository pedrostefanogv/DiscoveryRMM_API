using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Governança das MCP tools (servidor e agente): catálogo por escopo com
/// herança, enable/disable, rate limit e timeout.
/// </summary>
public interface IMcpToolGovernance
{
    /// <summary>Políticas efetivas (herdadas) por nome de tool no escopo.</summary>
    Task<IReadOnlyList<McpToolPolicy>> GetEffectivePoliciesAsync(McpToolScope scope, CancellationToken ct = default);

    /// <summary>Catálogo de tools (server + agent) com estado efetivo e local.</summary>
    Task<McpToolCatalog> GetCatalogAsync(McpToolScope scope, CancellationToken ct = default);

    /// <summary>Cria/atualiza a política da tool no escopo informado.</summary>
    Task<McpToolCatalog> SavePolicyAsync(string toolName, SaveMcpToolPolicyRequest request, CancellationToken ct = default);

    /// <summary>Remove a sobrescrita local (a tool volta a herdar).</summary>
    Task<bool> ResetPolicyAsync(string toolName, McpToolScope scope, CancellationToken ct = default);

    /// <summary>
    /// Consome uma chamada no rate limit da tool. Retorna false quando o limite
    /// por minuto foi excedido (a chamada deve ser recusada com erro para o LLM).
    /// O contador é distribuído (Redis) com fallback local quando o Redis está
    /// indisponível.
    /// </summary>
    Task<bool> TryConsumeRateLimitAsync(string toolName, McpToolScope scope, int maxCallsPerMinute);

    /// <summary>
    /// Quantas sobrescritas existem em escopos MAIS específicos que o informado —
    /// avisa o operador antes de desabilitar/bloquear no nível atual.
    /// </summary>
    Task<int> GetImpactAsync(string toolName, McpToolScope scope, CancellationToken ct = default);
}

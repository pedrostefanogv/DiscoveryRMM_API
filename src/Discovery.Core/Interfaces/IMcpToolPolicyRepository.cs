using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Repositório para consulta de políticas MCP tool por escopo.
/// </summary>
public interface IMcpToolPolicyRepository
{
    /// <summary>
    /// Retorna todas as políticas aplicáveis ao escopo (match exato ou global).
    /// Ordem de prioridade: agent > site > client > global (NULL em todos).
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
}

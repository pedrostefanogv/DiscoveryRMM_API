using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface ICommandRepository
{
    Task<AgentCommand?> GetByIdAsync(Guid id);
    Task<IEnumerable<AgentCommand>> GetPendingByAgentIdAsync(Guid agentId);

    /// <summary>
    /// Comandos ainda não confirmados (Pending/Sent/Running) de agentes que estão
    /// online agora e elegíveis a reentrega: criados depois de
    /// <paramref name="createdAfterUtc"/> e sem envio recente
    /// (nunca enviados antes de <paramref name="staleBeforeUtc"/>, ou último envio
    /// anterior a ele).
    /// </summary>
    Task<IReadOnlyList<AgentCommand>> GetRedeliveryCandidatesAsync(
        IReadOnlyCollection<Guid> agentIds,
        DateTime createdAfterUtc,
        DateTime staleBeforeUtc,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Comandos não confirmados mais antigos que <paramref name="createdBeforeUtc"/>.
    /// São comandos que ficaram presos (agente offline além da janela de
    /// reentrega) e devem ser encerrados para não poluir a fila nem o histórico
    /// com "em andamento" eterno.
    /// </summary>
    Task<IReadOnlyList<AgentCommand>> GetExpiredUnconfirmedAsync(
        DateTime createdBeforeUtc,
        int limit,
        CancellationToken ct = default);
    Task<IEnumerable<AgentCommand>> GetByAgentIdAsync(Guid agentId, int limit = 50);
    Task<AgentCommand> CreateAsync(AgentCommand command);
    Task UpdateStatusAsync(Guid id, CommandStatus status, string? result, int? exitCode, string? errorMessage);
}

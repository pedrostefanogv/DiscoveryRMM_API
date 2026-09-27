namespace Discovery.Core.Interfaces;

/// <summary>Resolve o responsável de um chamado por estratégia do departamento (round-robin/least-open).</summary>
public interface ITicketAssignmentService
{
    /// <summary>
    /// Estratégia de auto-atribuição configurada no departamento (int do enum
    /// TicketAssignmentStrategy) ou null quando o departamento não existe.
    /// </summary>
    Task<int?> GetStrategyAsync(Guid departmentId, CancellationToken ct = default);

    /// <summary>
    /// Resolve o responsável pela estratégia configurada no departamento.
    /// Retorna null quando a estratégia é None ou AiTriage (a triagem por IA
    /// roda de forma assíncrona e decide por conta própria).
    /// </summary>
    Task<Guid?> ResolveAssigneeAsync(Guid departmentId, CancellationToken ct = default);

    /// <summary>
    /// Resolve o responsável por uma estratégia determinística específica
    /// (1 = RoundRobin, 2 = LeastOpenTickets). Usado como fallback da triagem
    /// por IA e pela rede de segurança de chamados sem responsável.
    /// </summary>
    Task<Guid?> ResolveFallbackAsync(Guid departmentId, int fallbackStrategy, CancellationToken ct = default);
}

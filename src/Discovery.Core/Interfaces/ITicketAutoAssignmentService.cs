using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>Resolução da auto-atribuição na criação do chamado.</summary>
public sealed record AutoAssignmentResolution(
    Guid? AssignedToUserId,
    bool QueueAiTriage,
    int? Strategy);

/// <summary>
/// Ponto único da auto-atribuição na criação do chamado. Usado pelo fluxo da
/// API/console (TicketCommandService) e pelos chamados criados por alerta ou
/// evento (AlertToTicketService) — antes esses últimos não passavam por
/// estratégia alguma.
/// </summary>
public interface ITicketAutoAssignmentService
{
    /// <summary>
    /// Resolve a atribuição sem persistir nada: responsável determinístico
    /// (round-robin/least-open) ou indicação de enfileirar a triagem por IA.
    /// </summary>
    Task<AutoAssignmentResolution> ResolveAsync(
        Guid? departmentId, Guid? explicitAssignee, CancellationToken ct = default);

    /// <summary>
    /// Aplica a resolução em um chamado já persistido (fluxo de alerta/evento):
    /// atribui e salva, ou enfileira a triagem por IA.
    /// </summary>
    Task<bool> ApplyAfterCreateAsync(Ticket ticket, CancellationToken ct = default);
}

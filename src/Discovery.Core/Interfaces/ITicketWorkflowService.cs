using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço de domínio para transições de estado de workflow de tickets.
/// Encapsula validação de transição, gerenciamento de SLA hold/pause,
/// disparo de alertas e notificações.
/// </summary>
public interface ITicketWorkflowService
{
    /// <summary>
    /// Realiza a transição de estado de um ticket, gerenciando SLA hold,
    /// alertas PSADT e notificações.
    /// </summary>
    Task<Ticket> TransitionAsync(
        Guid ticketId,
        Guid targetStateId,
        Guid? changedByUserId,
        CancellationToken ct = default);
}

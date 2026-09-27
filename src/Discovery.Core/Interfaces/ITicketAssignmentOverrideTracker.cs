namespace Discovery.Core.Interfaces;

/// <summary>
/// Fecha o ciclo de feedback da triagem por IA: quando um humano troca (ou
/// remove) o responsável de um chamado que teve decisão aplicada pela IA,
/// marca a decisão como sobreposta. É o insumo da recalibração de pesos.
/// </summary>
public interface ITicketAssignmentOverrideTracker
{
    /// <summary>
    /// Marca como sobreposta a decisão aplicada mais recente do chamado quando o
    /// novo responsável difere do escolhido pela IA. Idempotente: a decisão só é
    /// marcada uma vez. Retorna true quando marcou.
    /// </summary>
    Task<bool> MarkIfOverriddenAsync(
        Guid ticketId, Guid? newAssigneeUserId, Guid? changedByUserId, CancellationToken ct = default);
}

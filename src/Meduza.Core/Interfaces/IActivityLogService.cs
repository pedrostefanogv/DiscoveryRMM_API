using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IActivityLogService
{
    /// <summary>
    /// Registra uma atividade de mudança de estado.
    /// </summary>
    Task<TicketActivityLog> LogStateChangeAsync(Guid ticketId, Guid? changedByUserId, Guid oldStateId, Guid newStateId);
    
    /// <summary>
    /// Registra uma atividade de atribuição de ticket.
    /// </summary>
    Task<TicketActivityLog> LogAssignmentAsync(Guid ticketId, Guid? changedByUserId, Guid? oldUserId, Guid? newUserId);
    
    /// <summary>
    /// Registra uma atividade genérica.
    /// </summary>
    Task<TicketActivityLog> LogActivityAsync(Guid ticketId, TicketActivityType type, Guid? changedByUserId, 
        string? oldValue = null, string? newValue = null, string? comment = null);
    
    /// <summary>
    /// Registra automaticamente uma mudança de prioridade.
    /// </summary>
    Task<TicketActivityLog> LogPriorityChangeAsync(Guid ticketId, Guid? changedByUserId, string oldPriority, string newPriority);
    
    /// <summary>
    /// Registra automaticamente uma mudança de departamento.
    /// </summary>
    Task<TicketActivityLog> LogDepartmentChangeAsync(Guid ticketId, Guid? changedByUserId, string oldDept, string newDept);
}

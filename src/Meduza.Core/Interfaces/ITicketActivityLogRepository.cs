using Meduza.Core.Entities;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface ITicketActivityLogRepository
{
    /// <summary>
    /// Registra uma nova atividade de ticket.
    /// </summary>
    Task<TicketActivityLog> LogAsync(TicketActivityLog log);
    
    /// <summary>
    /// Obtém o histórico completo de um ticket (timeline).
    /// </summary>
    Task<List<TicketActivityLog>> GetByTicketAsync(Guid ticketId);
    
    /// <summary>
    /// Obtém atividades de um ticket filtradas por tipo.
    /// </summary>
    Task<List<TicketActivityLog>> GetByTypeAsync(Guid ticketId, TicketActivityType type);
    
    /// <summary>
    /// Obtém atividades de um ticket filtrando por usuário que realizou a ação.
    /// </summary>
    Task<List<TicketActivityLog>> GetByUserAsync(Guid ticketId, Guid userId);
    
    /// <summary>
    /// Obtém atividades por range de data.
    /// </summary>
    Task<List<TicketActivityLog>> GetByDateRangeAsync(Guid ticketId, DateTime from, DateTime to);
    
    /// <summary>
    /// Obtém a últimaatividade de um ticket.
    /// </summary>
    Task<TicketActivityLog?> GetLastActivityAsync(Guid ticketId);
    
    /// <summary>
    /// Conta quantas mudanças ocorreram em um ticket.
    /// </summary>
    Task<int> GetActivityCountAsync(Guid ticketId);
}

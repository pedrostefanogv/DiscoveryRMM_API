using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ITicketRepository
{
    Task<Ticket?> GetByIdAsync(Guid id);
    Task<IEnumerable<Ticket>> GetByClientIdAsync(Guid clientId, Guid? workflowStateId = null);
    Task<IEnumerable<Ticket>> GetByAgentIdAsync(Guid agentId, Guid? workflowStateId = null);
    Task<IEnumerable<Ticket>> GetAllAsync(TicketFilterQuery filter);
    Task<IReadOnlyList<Ticket>> GetAllPageAsync(TicketFilterQuery filter);
    Task<Ticket> CreateAsync(Ticket ticket);
    Task UpdateAsync(Ticket ticket);
    Task DeleteAsync(Guid id);
    Task UpdateWorkflowStateAsync(Guid id, Guid workflowStateId, DateTime? closedAt = null);
    Task<IEnumerable<TicketComment>> GetCommentsAsync(Guid ticketId);

    /// <summary>Pagina comentários usando cursor (CreatedAt + Id).</summary>
    Task<IReadOnlyList<TicketComment>> GetCommentsPageAsync(Guid ticketId, string? cursor, int limit);

    Task<TicketComment> AddCommentAsync(TicketComment comment);
    
    /// <summary>
    /// Obtém todos os tickets abertos (não fechados) que possuem SLA configurado.
    /// Usado pelo SLA Monitoring Background Service.
    /// </summary>
    Task<List<Ticket>> GetOpenTicketsWithSlaAsync();

    /// <summary>Atualiza campos de SLA hold (pausa/retomada) no ticket.</summary>
    Task UpdateSlaHoldAsync(Guid id, DateTime? slaHoldStartedAt, int slaPausedSeconds);

    /// <summary>
    /// Persiste a transição de estado e o ajuste de SLA-hold ATOMICAMENTE
    /// (um único ExecuteUpdate), eliminando a corrida com close/reabertura.
    /// </summary>
    Task UpdateWorkflowStateWithSlaHoldAsync(Guid id, Guid workflowStateId, DateTime? closedAt, DateTime? slaHoldStartedAt, int slaPausedSeconds);

    /// <summary>Registra o momento da primeira resposta do atribuído.</summary>
    Task UpdateFirstRespondedAtAsync(Guid id, DateTime firstRespondedAt);

    /// <summary>KPI: contagens e métricas agrupadas para o dashboard de chamados.</summary>
    Task<TicketKpiResult> GetKpiAsync(Guid? clientId, Guid? departmentId, DateTime? since);

    /// <summary>
    /// KPI com os mesmos filtros da listagem e ACL. OBRIGATÓRIO implementar:
    /// uma implementação que ignore filtros/ACL fura a row-level security.
    /// </summary>
    Task<TicketKpiResult> GetKpiAsync(TicketFilterQuery filter);
}

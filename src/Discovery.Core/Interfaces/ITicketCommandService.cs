using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço de aplicação para commands de Tickets.
/// Encapsula a criação, atualização e orquestração de tickets,
/// para manter os handlers thin.
/// </summary>
public interface ITicketCommandService
{
    /// <summary>Cria um novo ticket e dispara eventos de domínio.</summary>
    Task<Ticket> CreateTicketAsync(
        string title, string description, Enums.TicketPriority priority,
        Guid clientId, Guid? siteId, Guid? agentId, Guid? departmentId,
        Guid? workflowProfileId, Guid? assignedToUserId, string? category,
        CancellationToken ct = default,
        string? submissionSnapshotMarkdown = null,
        Guid? templateId = null,
        string? templateName = null,
        Guid? requesterUserId = null);

    /// <summary>Atualiza campos de um ticket existente.</summary>
    Task<Ticket> UpdateTicketAsync(Guid ticketId, string? title, string? description,
        Enums.TicketPriority? priority, Guid? departmentId, Guid? workflowProfileId,
        Guid? assignedToUserId, string? category,
        bool clearDepartment = false, bool clearWorkflowProfile = false,
        CancellationToken ct = default,
        Guid? requesterUserId = null, bool clearRequester = false,
        Guid? agentId = null, bool clearAgent = false,
        Guid? changedByUserId = null);

    /// <summary>Adiciona um comentário a um ticket.</summary>
    Task<TicketComment> AddCommentAsync(Guid ticketId, string content, bool isInternal,
        Guid? userId, string? userName, CancellationToken ct = default);

    /// <summary>Atribui um ticket a um usuário.</summary>
    Task<Ticket> AssignTicketAsync(Guid ticketId, Guid? assignedToUserId,
        Guid? changedByUserId, CancellationToken ct = default);

    /// <summary>
    /// Backfill de SLA (correção A): aplica um perfil de workflow ativo aos
    /// chamados abertos do departamento que ainda estão sem perfil — caso típico
    /// de chamado criado antes de o departamento receber seu SLA. O SLA passa a
    /// contar do instante do backfill (mesma semântica da transferência).
    /// Retorna a quantidade de chamados afetados.
    /// </summary>
    Task<int> BackfillDepartmentProfileAsync(
        WorkflowProfile profile, Guid? changedByUserId = null, CancellationToken ct = default);
}

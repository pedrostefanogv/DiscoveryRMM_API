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
        CancellationToken ct = default);

    /// <summary>Atualiza campos de um ticket existente.</summary>
    Task<Ticket> UpdateTicketAsync(Guid ticketId, string? title, string? description,
        Enums.TicketPriority? priority, Guid? departmentId, Guid? workflowProfileId,
        Guid? assignedToUserId, string? category, CancellationToken ct = default);

    /// <summary>Adiciona um comentário a um ticket.</summary>
    Task<TicketComment> AddCommentAsync(Guid ticketId, string content, bool isInternal,
        Guid? userId, string? userName, CancellationToken ct = default);

    /// <summary>Atribui um ticket a um usuário.</summary>
    Task<Ticket> AssignTicketAsync(Guid ticketId, Guid? assignedToUserId,
        Guid? changedByUserId, CancellationToken ct = default);
}

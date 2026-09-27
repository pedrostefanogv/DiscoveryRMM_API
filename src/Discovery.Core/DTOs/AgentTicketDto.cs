using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

/// <summary>
/// Contrato público de chamado exposto ao agent (rotas /agent-auth/me/tickets).
/// Projeção explícita: evita vazar campos internos da entidade Ticket
/// (atribuição, solicitante, SLA, lixeira, template...) para a máquina do
/// cliente e desacopla o contrato do agent do schema do banco.
/// </summary>
public sealed record AgentTicketDto(
    Guid Id,
    Guid ClientId,
    Guid? SiteId,
    Guid? AgentId,
    string Title,
    string Description,
    TicketPriority Priority,
    string? Category,
    Guid WorkflowStateId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ClosedAt,
    int? Rating,
    string? RatingFeedback,
    DateTime? RatedAt,
    string? RatedBy,
    string? SubmissionSnapshotMarkdown);

public static class AgentTicketMapper
{
    /// <summary>Converte a entidade para o contrato público do agent.</summary>
    public static AgentTicketDto ToAgentTicketDto(this Ticket ticket) => new(
        ticket.Id,
        ticket.ClientId,
        ticket.SiteId,
        ticket.AgentId,
        ticket.Title,
        ticket.Description,
        ticket.Priority,
        ticket.Category,
        ticket.WorkflowStateId,
        ticket.CreatedAt,
        ticket.UpdatedAt,
        ticket.ClosedAt,
        ticket.Rating,
        ticket.RatingFeedback,
        ticket.RatedAt,
        ticket.RatedBy,
        ticket.SubmissionSnapshotMarkdown);
}

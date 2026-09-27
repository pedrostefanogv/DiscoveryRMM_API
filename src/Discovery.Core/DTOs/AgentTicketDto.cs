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
    // Departamento atual: a IA usa em get_department_fields.
    Guid? DepartmentId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ClosedAt,
    int? Rating,
    string? RatingFeedback,
    DateTime? RatedAt,
    string? RatedBy,
    string? SubmissionSnapshotMarkdown);

/// <summary>
/// Valor de um campo personalizado do departamento em um chamado, exposto ao
/// agent no detalhe (somente leitura).
/// </summary>
public sealed record AgentTicketFieldDto(
    Guid DefinitionId,
    string Label,
    string DataType,
    bool IsRequired,
    string? ValueJson);

/// <summary>
/// Resposta do mini questionário do template, exposta ao agent no detalhe
/// (somente leitura). Embedding e ValueJson não são expostos.
/// </summary>
public sealed record AgentTicketAnswerDto(
    Guid Id,
    string QuestionKey,
    string QuestionLabel,
    string? ValueText,
    DateTime CreatedAt);

/// <summary>
/// Comentário público de chamado no contrato do agent (sem campos internos,
/// sem id do chamado).
/// </summary>
public sealed record AgentTicketCommentDto(
    Guid Id,
    string Author,
    string Content,
    DateTime CreatedAt);

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
        ticket.DepartmentId,
        ticket.CreatedAt,
        ticket.UpdatedAt,
        ticket.ClosedAt,
        ticket.Rating,
        ticket.RatingFeedback,
        ticket.RatedAt,
        ticket.RatedBy,
        ticket.SubmissionSnapshotMarkdown);
}

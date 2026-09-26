using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.Tickets.Dtos;

/// <summary>
/// Read-optimized DTO for ticket details.
/// </summary>
public sealed record TicketDetailDto(
    Guid Id,
    Guid ClientId,
    Guid? SiteId,
    Guid? AgentId,
    string Title,
    string Description,
    string? Category,
    TicketPriority Priority,
    Guid WorkflowStateId,
    Guid? AssignedToUserId,
    DateTime? SlaExpiresAt,
    bool SlaBreached,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ClosedAt,
    int? DaysOpen,
    // Avaliação CSAT (0..5) + feedback textual.
    int? Rating = null,
    string? RatingFeedback = null,
    DateTime? RatedAt = null,
    string? RatedBy = null,
    // Snapshot markdown (somente leitura) do formulário/template enviado na abertura.
    string? SubmissionSnapshotMarkdown = null,
    // Template usado na abertura (null = abertura normal).
    Guid? TemplateId = null,
    // Snapshot do nome do template (preserva o histórico após exclusão).
    string? TemplateName = null,
    /// <summary>Solicitante (quem abriu). Nulo = legado ou aberto pelo chat/agent.</summary>
    Guid? RequesterUserId = null
);

/// <summary>
/// Read-optimized DTO for ticket list items (lightweight).
/// </summary>
public sealed record TicketListItemDto(
    Guid Id,
    Guid ClientId,
    Guid? SiteId,
    string Title,
    TicketPriority Priority,
    Guid WorkflowStateId,
    Guid? AssignedToUserId,
    bool SlaBreached,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    Guid? TemplateId = null,
    string? TemplateName = null
);

/// <summary>
/// Filter parameters for listing tickets (cursor-based pagination).
/// </summary>
public sealed record TicketListFilter(
    Guid? ClientId,
    Guid? SiteId,
    Guid? AgentId,
    Guid? DepartmentId,
    Guid? WorkflowStateId,
    Guid? AssignedToUserId,
    TicketPriority? Priority,
    bool? SlaBreached,
    bool? IsClosed,
    string? Text,
    string? Cursor,
    int Limit = 100
);
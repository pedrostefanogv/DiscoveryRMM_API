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
    int? DaysOpen
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
    DateTime? ClosedAt
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
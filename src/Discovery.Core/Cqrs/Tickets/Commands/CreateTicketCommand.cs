using Discovery.Core.Cqrs.Tickets.Dtos;

namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>
/// Command to create a new ticket.
/// </summary>
public sealed record CreateTicketCommand(
    string Title,
    string Description,
    Enums.TicketPriority Priority,
    Guid ClientId,
    Guid? SiteId,
    Guid? AgentId,
    Guid? DepartmentId,
    Guid? WorkflowProfileId,
    Guid? AssignedToUserId,
    string? Category
) : ICommand<Result<TicketDetailDto>>;
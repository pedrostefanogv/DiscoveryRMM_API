using Discovery.Core.Cqrs.Tickets.Dtos;

namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>
/// Command to update an existing ticket's properties.
/// </summary>
public sealed record UpdateTicketCommand(
    Guid Id,
    string? Title,
    string? Description,
    Enums.TicketPriority? Priority,
    Guid? DepartmentId,
    Guid? WorkflowProfileId,
    Guid? AssignedToUserId,
    string? Category,
    // B9: quando true, remove o vínculo mesmo sem enviar um novo Id.
    bool ClearDepartment = false,
    bool ClearWorkflowProfile = false
) : ICommand<Result<TicketDetailDto>>;
using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Tickets;

public sealed record GetMyTicketsQuery(Guid? WorkflowStateId) : IQuery<Result<object>>;
public sealed record GetMyTicketQuery(Guid TicketId) : IQuery<Result<object>>;
public sealed record CreateMyTicketCommand(
    string Title, string? Description, Guid? DepartmentId, Guid? WorkflowProfileId,
    string? Category, string? Priority) : ICommand<Result<object>>;
public sealed record AddMyTicketCommentCommand(Guid TicketId, string Content, bool? IsInternal) : ICommand<Result<object>>;
public sealed record GetMyTicketCommentsQuery(Guid TicketId) : IQuery<Result<object>>;
public sealed record UpdateMyTicketWorkflowStateCommand(Guid TicketId, Guid WorkflowStateId) : ICommand<Result<object>>;
public sealed record CloseAndRateMyTicketCommand(Guid TicketId, int? Rating, string? Feedback) : ICommand<Result<object>>;
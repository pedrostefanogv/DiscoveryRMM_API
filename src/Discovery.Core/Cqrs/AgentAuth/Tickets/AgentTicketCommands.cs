using System.Text.Json;
using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Tickets;

public sealed record GetMyTicketsQuery(Guid AgentId, Guid? WorkflowStateId) : IQuery<Result<object>>;
public sealed record GetMyTicketQuery(Guid AgentId, Guid TicketId) : IQuery<Result<object>>;
public sealed record GetMyTicketTemplatesQuery(Guid AgentId) : IQuery<Result<object>>;
public sealed record CreateMyTicketCommand(
    Guid AgentId, string Title, string? Description, Guid? DepartmentId, Guid? WorkflowProfileId,
    string? Category, string? Priority,
    Guid? TemplateId = null,
    IReadOnlyDictionary<Guid, JsonElement>? CustomFieldValues = null,
    IReadOnlyDictionary<string, JsonElement>? TemplateAnswers = null) : ICommand<Result<object>>;
public sealed record AddMyTicketCommentCommand(Guid AgentId, Guid TicketId, string Content, bool? IsInternal) : ICommand<Result<object>>;
public sealed record GetMyTicketCommentsQuery(Guid AgentId, Guid TicketId) : IQuery<Result<object>>;
public sealed record UpdateMyTicketWorkflowStateCommand(Guid AgentId, Guid TicketId, Guid WorkflowStateId) : ICommand<Result<object>>;
public sealed record CloseAndRateMyTicketCommand(Guid AgentId, Guid TicketId, int? Rating, string? Feedback, Guid? WorkflowStateId = null) : ICommand<Result<object>>;

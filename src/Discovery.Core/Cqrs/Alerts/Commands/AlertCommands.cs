using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Alerts.Queries;

namespace Discovery.Core.Cqrs.Alerts.Commands;

public sealed record CreateAlertCommand(
    string Title, string Description, string Severity,
    Guid? AgentId, Guid? ClientId, Guid? SiteId
) : ICommand<Result<AlertDetailDto>>;

public sealed record DispatchAlertCommand(Guid AlertId) : ICommand<Result<VoidResult>>;

public sealed record CancelAlertCommand(Guid AlertId) : ICommand<Result<VoidResult>>;

public sealed record CreateTicketFromAlertCommand(
    Guid AlertId, string Title, string Description, Guid? AssignToUserId
) : ICommand<Result<AlertDetailDto>>;

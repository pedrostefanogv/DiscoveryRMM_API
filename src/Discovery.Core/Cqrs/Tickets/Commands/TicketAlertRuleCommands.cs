using Discovery.Core.Cqrs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.Tickets.Commands;

public sealed record CreateTicketAlertRuleCommand(
    Guid WorkflowStateId, string Title, string Message,
    PsadtAlertType AlertType, int? TimeoutSeconds, string? ActionsJson,
    string? DefaultAction, string Icon, AlertScopeType ScopePreference, bool IsEnabled
) : ICommand<Result<TicketAlertRule>>;

public sealed record UpdateTicketAlertRuleCommand(
    Guid Id, Guid WorkflowStateId, string Title, string Message,
    PsadtAlertType AlertType, int? TimeoutSeconds, string? ActionsJson,
    string? DefaultAction, string Icon, AlertScopeType ScopePreference, bool IsEnabled
) : ICommand<Result<TicketAlertRule>>;

public sealed record ToggleTicketAlertRuleCommand(Guid Id) : ICommand<Result<TicketAlertRule>>;
public sealed record DeleteTicketAlertRuleCommand(Guid Id) : ICommand<Result<VoidResult>>;

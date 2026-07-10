using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Notifications.Commands;

public sealed record MarkNotificationReadCommand(
    Guid Id,
    Guid? RecipientUserId = null,
    Guid? RecipientAgentId = null
) : ICommand<Result<VoidResult>>;

public sealed record DeleteNotificationCommand(
    Guid Id,
    Guid? RecipientUserId = null,
    Guid? RecipientAgentId = null
) : ICommand<Result<VoidResult>>;

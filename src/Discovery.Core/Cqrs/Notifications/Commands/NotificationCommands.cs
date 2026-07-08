using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Notifications.Commands;

public sealed record MarkNotificationReadCommand(Guid Id, Guid? RecipientUserId = null)
    : ICommand<Result<VoidResult>>;

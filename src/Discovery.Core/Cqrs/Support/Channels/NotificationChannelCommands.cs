using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Channels;

public sealed record CreateNotificationChannelCommand(
    string Name, string Type, bool IsActive, string EventsJson, string ConfigJson, string? CreatedBy)
    : ICommand<Result<NotificationChannelDto>>;

public sealed record UpdateNotificationChannelCommand(
    Guid Id, string Name, string Type, bool IsActive, string EventsJson, string ConfigJson)
    : ICommand<Result<NotificationChannelDto>>;

public sealed record DeleteNotificationChannelCommand(Guid Id) : ICommand<Result<VoidResult>>;

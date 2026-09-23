using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Channels;

public sealed record ListNotificationChannelsQuery : IQuery<Result<IReadOnlyList<NotificationChannelDto>>>;

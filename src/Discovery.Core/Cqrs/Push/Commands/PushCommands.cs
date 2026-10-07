using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Push;

namespace Discovery.Core.Cqrs.Push.Commands;

public sealed record RegisterPushSubscriptionCommand(
    Guid UserId,
    string Endpoint,
    string P256dh,
    string Auth,
    string? UserAgent = null
) : ICommand<Result<PushSubscriptionDto>>;

public sealed record DeletePushSubscriptionCommand(
    Guid UserId,
    string Endpoint
) : ICommand<Result<VoidResult>>;

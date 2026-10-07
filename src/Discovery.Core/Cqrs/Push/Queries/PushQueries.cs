using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Push;

namespace Discovery.Core.Cqrs.Push.Queries;

public sealed record GetPushStatusQuery(Guid UserId) : IQuery<Result<PushConfigDto>>;

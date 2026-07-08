using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.NatsAuth.Queries;

public sealed record GetNatsStatusQuery : IQuery<Result<NatsStatusDto>>;
public sealed record NatsStatusDto(bool Connected, string? ServerUrl, DateTime? LastPing);

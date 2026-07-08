using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Fanout.Commands;

public sealed record SendSiteFanoutCommand(Guid SiteId, string CommandType, string? Payload, DateTime? ExpiresAtUtc) : ICommand<Result<FanoutResponseDto>>;
public sealed record SendClientFanoutCommand(Guid ClientId, string CommandType, string? Payload, DateTime? ExpiresAtUtc) : ICommand<Result<FanoutResponseDto>>;
public sealed record SendGlobalFanoutCommand(string CommandType, string? Payload, DateTime? ExpiresAtUtc) : ICommand<Result<FanoutResponseDto>>;

public sealed record FanoutResponseDto(Guid DispatchId, string Subject, string TargetScope, string? IdempotencyKey);
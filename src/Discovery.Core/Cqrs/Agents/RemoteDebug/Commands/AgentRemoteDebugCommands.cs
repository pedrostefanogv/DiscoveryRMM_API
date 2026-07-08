using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.RemoteDebug.Commands;

public sealed record StartRemoteDebugCommand(Guid AgentId, Guid UserId, string? Payload) : ICommand<Result<RemoteDebugResponseDto>>;
public sealed record StopRemoteDebugCommand(Guid AgentId, Guid SessionId, Guid UserId) : ICommand<Result<VoidResult>>;

public sealed record RemoteDebugResponseDto(Guid SessionId, string Subject, int Port, string Status);
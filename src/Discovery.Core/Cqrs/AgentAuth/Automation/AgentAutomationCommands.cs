using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Automation;

public sealed record SyncAutomationPolicyCommand(Guid AgentId, object? Request, string? CorrelationId, string? Username, string? IpAddress) : ICommand<Result<object>>;
public sealed record GetAgentCommandsQuery(Guid AgentId, int Limit = 50) : IQuery<Result<object>>;
public sealed record AckAutomationExecutionCommand(Guid AgentId, Guid CommandId, object? Request, string? CorrelationId) : ICommand<Result<VoidResult>>;
public sealed record CompleteAutomationExecutionCommand(Guid AgentId, Guid CommandId, object? Request, string? CorrelationId) : ICommand<Result<VoidResult>>;
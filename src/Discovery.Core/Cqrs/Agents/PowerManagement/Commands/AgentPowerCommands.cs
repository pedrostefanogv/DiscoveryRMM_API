using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.PowerManagement.Commands;

public sealed record RestartAgentCommand(Guid AgentId, string? Reason) : ICommand<Result<VoidResult>>;
public sealed record ShutdownAgentCommand(Guid AgentId, string? Reason) : ICommand<Result<VoidResult>>;
public sealed record WakeOnLanCommand(Guid AgentId) : ICommand<Result<VoidResult>>;
using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Automation.Commands;

public sealed record RunAutomationTaskCommand(Guid AgentId, Guid TaskId) : ICommand<Result<AutomationExecutionDto>>;
public sealed record RunAutomationScriptCommand(Guid AgentId, Guid ScriptId) : ICommand<Result<AutomationExecutionDto>>;
public sealed record ForceAutomationSyncCommand(Guid AgentId, string? TaskIds) : ICommand<Result<VoidResult>>;
public sealed record RefreshAgentDataCommand(Guid AgentId, bool ListeningPorts = false, bool OpenConnections = false, bool Software = false, bool Printers = false, bool Hardware = false) : ICommand<Result<VoidResult>>;

public sealed record AutomationExecutionDto(Guid ExecutionId, string Status, DateTime StartedAt);
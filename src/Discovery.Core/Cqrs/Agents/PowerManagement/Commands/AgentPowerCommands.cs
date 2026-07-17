using System.Text.Json.Serialization;
using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.PowerManagement.Commands;

public sealed record RestartAgentCommand(Guid AgentId, [property: JsonPropertyName("message")] string? Reason) : ICommand<Result<VoidResult>>;
public sealed record ShutdownAgentCommand(Guid AgentId, [property: JsonPropertyName("message")] string? Reason) : ICommand<Result<VoidResult>>;
public sealed record WakeOnLanCommand(Guid AgentId) : ICommand<Result<VoidResult>>;
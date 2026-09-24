using System.Text.Json.Serialization;
using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.PowerManagement.Commands;

public sealed record RestartAgentCommand(
    Guid AgentId,
    [property: JsonPropertyName("delaySeconds")] int DelaySeconds = 15,
    [property: JsonPropertyName("force")] bool Force = false,
    // notifyUser=true faz o agent exibir o aviso Fluent com contador antes de reiniciar.
    [property: JsonPropertyName("notifyUser")] bool NotifyUser = true,
    [property: JsonPropertyName("message")] string? Reason = null) : ICommand<Result<VoidResult>>;

public sealed record ShutdownAgentCommand(
    Guid AgentId,
    [property: JsonPropertyName("delaySeconds")] int DelaySeconds = 30,
    [property: JsonPropertyName("force")] bool Force = false,
    // notifyUser=true faz o agent exibir o aviso Fluent com contador antes de desligar.
    [property: JsonPropertyName("notifyUser")] bool NotifyUser = true,
    [property: JsonPropertyName("message")] string? Reason = null) : ICommand<Result<VoidResult>>;
public sealed record WakeOnLanCommand(Guid AgentId) : ICommand<Result<VoidResult>>;
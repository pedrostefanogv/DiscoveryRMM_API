using System.Text.Json.Serialization;
using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.PowerManagement.Commands;

public sealed record RestartAgentCommand(
    Guid AgentId,
    [property: JsonPropertyName("delaySeconds")] int DelaySeconds = 15,
    [property: JsonPropertyName("force")] bool Force = false,
    // notifyUser=true faz o agent exibir o aviso Fluent com contador antes de reiniciar.
    [property: JsonPropertyName("notifyUser")] bool NotifyUser = true,
    [property: JsonPropertyName("message")] string? Reason = null) : ICommand<Result<Guid>>;

public sealed record ShutdownAgentCommand(
    Guid AgentId,
    [property: JsonPropertyName("delaySeconds")] int DelaySeconds = 30,
    [property: JsonPropertyName("force")] bool Force = false,
    // notifyUser=true faz o agent exibir o aviso Fluent com contador antes de desligar.
    [property: JsonPropertyName("notifyUser")] bool NotifyUser = true,
    [property: JsonPropertyName("message")] string? Reason = null) : ICommand<Result<Guid>>;
/// <summary>
/// Comando genérico de energia. O verbo vai no campo <c>action</c> do payload
/// (hoje só <c>restart-firmware</c>: reinício direto no setup do firmware —
/// exige boot UEFI, validado no agent, que devolve falha tipada em Legacy/CSM).
/// Novos modos de energia entram como novos verbos, sem novo CommandType.
/// </summary>
public sealed record PowerActionCommand(
    Guid AgentId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("delaySeconds")] int DelaySeconds = 0,
    [property: JsonPropertyName("force")] bool Force = false,
    [property: JsonPropertyName("notifyUser")] bool NotifyUser = false,
    [property: JsonPropertyName("message")] string? Reason = null) : ICommand<Result<Guid>>;

public sealed record WakeOnLanCommand(Guid AgentId) : ICommand<Result<VoidResult>>;
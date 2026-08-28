using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.AiChat;

public sealed record ChatSyncCommand(Guid AgentId, string Message, string? SessionId, int? MaxTokens, Guid? DepartmentId, string? ClientIp) : ICommand<Result<object>>;
public sealed record ChatAsyncCommand(Guid AgentId, string Message, string? SessionId, int? MaxTokens, Guid? DepartmentId) : ICommand<Result<object>>;
public sealed record GetAiChatJobQuery(Guid AgentId, Guid JobId) : IQuery<Result<object>>;

/// <summary>
/// Comando para streaming multi-round (Server-Managed Agent Loop).
/// Round 1: Message preenchida, ToolResults null.
/// Rounds 2+: Message null, ToolResults preenchido com resultados das tools executadas pelo agent.
/// </summary>
public sealed record ChatStreamCommand(
    Guid AgentId,
    string? Message,
    string? SessionId,
    List<ToolResultDto>? ToolResults,
    Guid? DepartmentId,
    string? ClientIp,
    string? SystemNote);

/// <summary>
/// Resultado de uma tool executada pelo agent no fluxo multi-round.
/// </summary>
public record ToolResultDto(
    string CallId,
    string Name,
    string Result);

/// <summary>
/// Comando para registro de tools do agent na API.
/// Enviado pelo agent no startup para expor suas MCP tools locais à IA.
/// </summary>
public sealed record RegisterAgentToolsCommand(
    Guid AgentId,
    List<AgentToolDto> Tools);

/// <summary>
/// Definição de uma tool do agent registrada na API.
/// </summary>
public record AgentToolDto(
    string Name,
    string Description,
    System.Text.Json.JsonElement ParametersSchema);
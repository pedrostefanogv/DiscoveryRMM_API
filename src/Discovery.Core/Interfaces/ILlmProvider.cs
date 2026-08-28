namespace Discovery.Core.Interfaces;

public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        LlmOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retorna tokens da resposta incrementalmente (SSE streaming).
    /// Não suporta tool calls — use StreamWithToolsAsync para isso.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        LlmOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming SSE que suporta tool calls. Emite eventos estruturados (tokens, tool_calls, done)
    /// em vez de apenas strings. O caller deve fazer o loop de tool calls.
    /// </summary>
    IAsyncEnumerable<LlmStreamEvent> StreamWithToolsAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        LlmOptions options,
        CancellationToken cancellationToken = default);
}

public record LlmMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    string? ToolName = null,
    List<LlmAssistantToolCall>? ToolCalls = null);

/// <summary>
/// Representa um tool call emitido pelo assistant (para serialização no histórico).
/// Necessário para que o payload OpenAI contenha assistant.tool_calls antes de tool results.
/// </summary>
public record LlmAssistantToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

public record LlmOptions(
    int MaxTokens = 1000,
    double Temperature = 0.7,
    string? Model = null,
    string? BaseUrl = null,
    string? ApiKey = null,
    bool EnableTools = false,
    List<LlmTool>? Tools = null,
    string? OpenRouterReferer = null,
    string? OpenRouterTitle = null,
    string? OpenRouterCategories = null,
    string? Provider = null,
    string? SessionId = null,
    int TimeoutMs = 0);

public record LlmTool(
    string Name,
    string Description,
    object Schema);

public record LlmResponse(
    string Content,
    int TokensUsed,
    string ModelVersion,
    List<LlmToolCall>? ToolCalls = null);

public record LlmToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

/// <summary>
/// Evento emitido durante streaming SSE com suporte a tool calls.
/// Type = "token"      → Content contém fragmento de texto.
/// Type = "tool_calls" → ToolCalls contém as tool calls (finish_reason=tool_calls).
/// Type = "done"       → Fim do streaming (finish_reason=stop).
/// </summary>
public record LlmStreamEvent(
    string Type,
    string? Content = null,
    List<LlmToolCall>? ToolCalls = null,
    int? TokensUsed = null);

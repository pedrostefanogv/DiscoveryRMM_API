namespace Discovery.Core.DTOs;

// Request do agent para chat síncrono
public record AgentChatRequest(
    string Message,
    Guid? SessionId = null, // null = nova sessão
    int? MaxTokens = 1000,
    Guid? DepartmentId = null // opcional — contexto departamental para RAG da KB
);

// Resposta síncrona (curta)
public record AgentChatSyncResponse(
    Guid SessionId,
    string AssistantMessage,
    int TokensUsed,
    int ConversationTokensTotal,
    int LatencyMs
);

// Request assíncrono (longa)
public record AgentChatAsyncRequest(
    string Message,
    Guid? SessionId = null,
    int? MaxTokens = 2000
);

// Status do job assíncrono
public record AgentChatJobStatus(
    Guid JobId,
    string Status, // Pending, Processing, Completed, Failed, Timeout
    Guid SessionId,
    string? AssistantMessage,
    int? TokensUsed,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? CompletedAt
);

// ── SSE Streaming ─────────────────────────────────────────────────────────────

/// <summary>
/// Chunk emitido durante streaming SSE.
/// Type = "token"            → Content contém o fragmento de texto incremental.
/// Type = "tool_call_start"  → ToolCallId + ToolName preenchidos (início de tool call).
/// Type = "tool_call_delta"  → ToolArgumentsDelta com fragmento incremental de argumentos.
/// Type = "tool_call_end"    → ToolCallId preenchido (fim de tool call).
/// Type = "tool_call"        → ToolCallId + ToolName + ToolArgumentsDelta (args JSON completo). Agent deve executar.
/// Type = "tool_result"      → ToolCallId + ToolResult preenchidos (resultado da tool).
/// Type = "round_end"        → SessionId preenchido. Stream fecha, agent deve enviar próximo POST com ToolResults.
/// Type = "a2ui"             → A2uiJson contém uma mensagem A2UI (createSurface/updateComponents/updateDataModel/deleteSurface).
/// Type = "done"             → SessionId, TokensUsed e LatencyMs estão preenchidos.
/// Type = "error"            → Error contém a mensagem de erro.
/// </summary>
public record AiChatStreamChunk(
    string Type,
    string? Content = null,
    Guid? SessionId = null,
    int? TokensUsed = null,
    int? LatencyMs = null,
    string? Error = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolArgumentsDelta = null,
    string? ToolResult = null,
    string? A2uiJson = null
);

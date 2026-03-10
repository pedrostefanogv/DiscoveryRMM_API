namespace Meduza.Core.DTOs;

// Request do agent para chat síncrono
public record AgentChatRequest(
    string Message,
    Guid? SessionId = null, // null = nova sessão
    int? MaxTokens = 1000
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

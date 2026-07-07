using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço para processamento de chat IA integrado com agents
/// Orquestra chamadas OpenAI, gerencia histórico e processa tool calls MCP
/// </summary>
public interface IAiChatService
{
    /// <summary>
    /// Processa uma mensagem de chat síncrona (rápida)
    /// </summary>
    Task<AgentChatSyncResponse> ProcessSyncAsync(
        Guid agentId, 
        string message, 
        Guid? sessionId,
        string? createdByIp = null,
        int? requestMaxTokens = null,
        Guid? departmentId = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Processa uma mensagem de chat assíncrona (longa)
    /// Cria um job e retorna imediatamente o JobId
    /// </summary>
    Task<Guid> ProcessAsyncAsync(
        Guid agentId, 
        string message, 
        Guid? sessionId,
        int? requestMaxTokens = null,
        Guid? departmentId = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Consulta o status de um job assíncrono
    /// </summary>
    Task<AgentChatJobStatus> GetJobStatusAsync(
        Guid jobId, 
        Guid agentId, 
        CancellationToken ct);
    
    /// <summary>
    /// Processa uma mensagem para contexto de ticket (triagem/resumo/sugestão).
    /// Diferente do chat do agent, tem contextos mais focados sem histórico persistente.
    /// </summary>
    Task<LlmResponse> ProcessTicketPromptAsync(
        string systemPrompt,
        string userMessage,
        Guid siteId,
        int maxTokens,
        double temperature,
        Guid? departmentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Responde via SSE streaming — emite chunks incrementais enquanto o LLM gera tokens.
    /// Suporta tool calls (loop de MCP tools) e RAG departamental.
    /// </summary>
    IAsyncEnumerable<AiChatStreamChunk> StreamAsync(
        Guid agentId,
        string message,
        Guid? sessionId,
        Guid? departmentId = null,
        CancellationToken ct = default);
}


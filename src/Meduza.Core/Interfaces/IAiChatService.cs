using Meduza.Core.DTOs;

namespace Meduza.Core.Interfaces;

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
        CancellationToken ct);
    
    /// <summary>
    /// Processa uma mensagem de chat assíncrona (longa)
    /// Cria um job e retorna imediatamente o JobId
    /// </summary>
    Task<Guid> ProcessAsyncAsync(
        Guid agentId, 
        string message, 
        Guid? sessionId, 
        CancellationToken ct);
    
    /// <summary>
    /// Consulta o status de um job assíncrono
    /// </summary>
    Task<AgentChatJobStatus> GetJobStatusAsync(
        Guid jobId, 
        Guid agentId, 
        CancellationToken ct);

    /// <summary>
    /// Responde via SSE streaming — emite chunks incrementais enquanto o LLM gera tokens.
    /// Não suporta tool calls; contexto RAG é injetado no system prompt antes de iniciar.
    /// </summary>
    IAsyncEnumerable<AiChatStreamChunk> StreamAsync(
        Guid agentId,
        string message,
        Guid? sessionId,
        CancellationToken ct);
}

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
    /// <param name="agentId">ID do agent que envia a mensagem</param>
    /// <param name="message">Mensagem do usuário (max 2KB)</param>
    /// <param name="sessionId">ID da sessão existente ou null para nova sessão</param>
    /// <param name="ct">Token de cancelamento</param>
    /// <returns>Resposta da IA com SessionId, mensagem e métricas</returns>
    Task<AgentChatSyncResponse> ProcessSyncAsync(
        Guid agentId, 
        string message, 
        Guid? sessionId, 
        CancellationToken ct);
    
    /// <summary>
    /// Processa uma mensagem de chat assíncrona (longa)
    /// Cria um job e retorna imediatamente o JobId
    /// </summary>
    /// <param name="agentId">ID do agent que envia a mensagem</param>
    /// <param name="message">Mensagem do usuário (max 2KB)</param>
    /// <param name="sessionId">ID da sessão existente ou null para nova sessão</param>
    /// <param name="ct">Token de cancelamento</param>
    /// <returns>JobId para consulta posterior</returns>
    Task<Guid> ProcessAsyncAsync(
        Guid agentId, 
        string message, 
        Guid? sessionId, 
        CancellationToken ct);
    
    /// <summary>
    /// Consulta o status de um job assíncrono
    /// </summary>
    /// <param name="jobId">ID do job</param>
    /// <param name="agentId">ID do agent (para validação de autorização)</param>
    /// <param name="ct">Token de cancelamento</param>
    /// <returns>Status do job com resultado se concluído</returns>
    Task<AgentChatJobStatus> GetJobStatusAsync(
        Guid jobId, 
        Guid agentId, 
        CancellationToken ct);
}

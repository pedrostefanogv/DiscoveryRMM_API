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
    /// Igual a <see cref="ProcessTicketPromptAsync"/>, mas permite response_format
    /// (ex.: "json_object") — usado pelos fluxos que exigem saída estruturada,
    /// como a triagem de atribuição por IA.
    /// </summary>
    Task<LlmResponse> ProcessTicketPromptJsonAsync(
        string systemPrompt,
        string userMessage,
        Guid siteId,
        int maxTokens,
        double temperature,
        string? responseFormat,
        Guid? departmentId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Responde via SSE streaming — emite chunks incrementais enquanto o LLM gera tokens.
    /// Suporta tool calls (loop de MCP tools) e RAG departamental.
    /// </summary>
    /// <param name="images">
    /// Data URLs (data:image/...) anexadas pelo usuário a esta mensagem (ex.:
    /// print de tela). São enviadas ao LLM como conteúdo multimodal.
    /// </param>
    IAsyncEnumerable<AiChatStreamChunk> StreamAsync(
        Guid agentId,
        string message,
        Guid? sessionId,
        Guid? departmentId = null,
        string? systemNote = null,
        List<string>? images = null,
        CancellationToken ct = default);

    /// <summary>
    /// Registra as tools MCP disponíveis de um agent para exposição à IA.
    /// </summary>
    Task RegisterAgentToolsAsync(Guid agentId, Guid siteId, List<AgentToolRegistration> tools, CancellationToken ct = default);

    /// <summary>
    /// Multi-round streaming (Server-Managed Agent Loop).
    /// Round 1: message != null, toolResults null.
    /// Rounds 2+: message null, toolResults preenchido.
    /// </summary>
    IAsyncEnumerable<AiChatStreamChunk> StreamMultiRoundAsync(
        Guid agentId, string? message, Guid? sessionId,
        List<ToolResultItem>? toolResults, Guid? departmentId = null, string? systemNote = null, CancellationToken ct = default);
}

/// <summary>
/// Registro de uma tool MCP do agent para exposição à IA.
/// </summary>
public record AgentToolRegistration(
    string Name,
    string Description,
    string ParametersSchemaJson);

/// <summary>
/// Resultado de uma tool executada pelo agent no fluxo multi-round.
/// </summary>
public record ToolResultItem(
    string CallId,
    string Name,
    string Result);


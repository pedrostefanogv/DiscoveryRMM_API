using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Orquestrador de streaming SSE: coordena capability resolution,
/// rate limiting e delegação entre streaming single-round e multi-round.
/// </summary>
public class AiChatStreamingOrchestrator
{
    private readonly AiChatCapabilityResolver _capabilityResolver;
    private readonly AiChatSingleRoundStreamer _singleRoundStreamer;
    private readonly AiChatMultiRoundStreamer _multiRoundStreamer;

    public AiChatStreamingOrchestrator(
        AiChatCapabilityResolver capabilityResolver,
        AiChatSingleRoundStreamer singleRoundStreamer,
        AiChatMultiRoundStreamer multiRoundStreamer)
    {
        _capabilityResolver = capabilityResolver;
        _singleRoundStreamer = singleRoundStreamer;
        _multiRoundStreamer = multiRoundStreamer;
    }

    internal AiChatStreamingOrchestrator(
        IAiChatSessionRepository sessionRepository,
        IAiChatMessageRepository messageRepository,
        IAgentRepository agentRepository,
        ISiteRepository siteRepository,
        ILlmProvider llmProvider,
        IMcpToolExecutor mcpToolExecutor,
        IMcpToolGovernance governance,
        ILogger<AiChatService> logger,
        AiChatSystemPromptBuilder promptBuilder,
        AiChatToolOrchestrator toolOrchestrator,
        AiChatQuickReply quickReply,
        IAiTokenBudgetResolver tokenBudgetResolver,
        IMemoryCache memoryCache,
        IAiModelCatalogService modelCatalog)
    {
        _capabilityResolver = new AiChatCapabilityResolver(
            mcpToolExecutor, modelCatalog, tokenBudgetResolver, llmProvider, logger);
        var rateLimiter = new AiChatToolRateLimiter(governance, logger);
        _singleRoundStreamer = new AiChatSingleRoundStreamer(
            sessionRepository, messageRepository, agentRepository, siteRepository,
            llmProvider, mcpToolExecutor, logger, promptBuilder, toolOrchestrator,
            quickReply, memoryCache, _capabilityResolver, rateLimiter);
        _multiRoundStreamer = new AiChatMultiRoundStreamer(
            sessionRepository, messageRepository, agentRepository,
            llmProvider, mcpToolExecutor, logger, promptBuilder, toolOrchestrator,
            memoryCache, _capabilityResolver, rateLimiter);
    }

    /// <summary>
    /// Critério de propagação de token de texto do LLM. NÃO usar
    /// IsNullOrWhiteSpace aqui: deltas contendo somente espaço/quebra de linha
    /// (" ", "\n", "\n\n") são legítimos e separam palavras, itens de lista e
    /// linhas de tabela markdown. Descartá-los colava o texto na saída do chat
    /// ("últimas24h", "travando.2.", título|tabela na mesma linha) — bug visto
    /// em produção em 19-20/09/2026 (a via sync, sem este filtro, saía limpa).
    /// Apenas conteúdo vazio (null/"") é descartado.
    /// </summary>
    public static bool IsTextToken(LlmStreamEvent evt)
        => evt.Type == "token" && !string.IsNullOrEmpty(evt.Content);

    internal Task<(string Content, int TokensUsed)> TryReemitA2uiDefinitionAsync(
        string systemPrompt,
        List<LlmMessage> llmMessages,
        AIIntegrationSettings aiSettings,
        int maxTokens,
        Guid sessionId,
        string previousOutput,
        IReadOnlyCollection<string> missingSurfaces,
        CancellationToken ct)
        => _capabilityResolver.TryReemitA2uiDefinitionAsync(
            systemPrompt, llmMessages, aiSettings, maxTokens, sessionId,
            previousOutput, missingSurfaces, ct);

    public IAsyncEnumerable<AiChatStreamChunk> StreamAsync(
        Guid agentId, string message, Guid? sessionId,
        Func<Guid, CancellationToken, Task<AIIntegrationSettings>> resolveAiSettings,
        Guid? departmentId = null,
        string? systemNote = null,
        List<string>? images = null,
        CancellationToken ct = default,
        int maxImageBase64Chars = 0)
        => _singleRoundStreamer.StreamAsync(
            agentId, message, sessionId, resolveAiSettings,
            departmentId, systemNote, images, ct, maxImageBase64Chars);

    public IAsyncEnumerable<AiChatStreamChunk> StreamMultiRoundAsync(
        Guid agentId, string? message, Guid? sessionId,
        List<ToolResultItem>? toolResults,
        Func<Guid, CancellationToken, Task<AIIntegrationSettings>> resolveAiSettings,
        Guid? departmentId = null,
        string? systemNote = null,
        CancellationToken ct = default)
        => _multiRoundStreamer.StreamMultiRoundAsync(
            agentId, message, sessionId, toolResults, resolveAiSettings,
            departmentId, systemNote, ct);
}

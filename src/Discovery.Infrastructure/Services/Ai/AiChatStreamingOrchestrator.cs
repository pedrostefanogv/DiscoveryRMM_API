using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Orquestrador de streaming SSE: gerencia o loop de tool calls,
/// delegação multi-round para o agent, XML fallback e persistência pós-stream.
/// </summary>
public class AiChatStreamingOrchestrator
{
    private readonly IAiChatSessionRepository _sessionRepository;
    private readonly IAiChatMessageRepository _messageRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly ILlmProvider _llmProvider;
    private readonly IMcpToolExecutor _mcpToolExecutor;
    private readonly ILogger<AiChatService> _logger;
    private readonly AiChatSystemPromptBuilder _promptBuilder;
    private readonly AiChatToolOrchestrator _toolOrchestrator;
    private readonly AiChatQuickReply _quickReply;

    public AiChatStreamingOrchestrator(
        IAiChatSessionRepository sessionRepository,
        IAiChatMessageRepository messageRepository,
        IAgentRepository agentRepository,
        ISiteRepository siteRepository,
        ILlmProvider llmProvider,
        IMcpToolExecutor mcpToolExecutor,
        ILogger<AiChatService> logger,
        AiChatSystemPromptBuilder promptBuilder,
        AiChatToolOrchestrator toolOrchestrator,
        AiChatQuickReply quickReply)
    {
        _sessionRepository = sessionRepository;
        _messageRepository = messageRepository;
        _agentRepository = agentRepository;
        _siteRepository = siteRepository;
        _llmProvider = llmProvider;
        _mcpToolExecutor = mcpToolExecutor;
        _logger = logger;
        _promptBuilder = promptBuilder;
        _toolOrchestrator = toolOrchestrator;
        _quickReply = quickReply;
    }

    public async IAsyncEnumerable<AiChatStreamChunk> StreamAsync(
        Guid agentId, string message, Guid? sessionId,
        Func<Guid, CancellationToken, Task<AIIntegrationSettings>> resolveAiSettings,
        Guid? departmentId = null,
        string? systemNote = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var startTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        AiChatSession? session = null;
        string? systemPrompt = null;
        List<LlmMessage>? llmMessages = null;
        int nextSeq = 1;
        bool setupOk = false;
        string? setupError = null;
        AIIntegrationSettings? aiSettings = null;
        Guid scopeClientId = Guid.Empty;
        Guid scopeSiteId = Guid.Empty;
        int maxIterations = AiChatConstants.DefaultMaxToolCallIterations;

        try
        {
            AiChatGuardrails.ValidateUserInput(message, AiChatConstants.MaxMessageSizeBytes);

            var agent = await _agentRepository.GetByIdAsync(agentId);
            if (agent == null) throw new ArgumentException($"Agent {agentId} não encontrado");

            var site = await _siteRepository.GetByIdAsync(agent.SiteId);
            if (site == null) throw new ArgumentException($"Site {agent.SiteId} não encontrado");

            scopeSiteId = agent.SiteId;
            scopeClientId = site.ClientId;
            aiSettings = await resolveAiSettings(agent.SiteId, ct);

            if (!aiSettings.Enabled || !aiSettings.ChatAIEnabled)
                throw new InvalidOperationException("Chat IA está desabilitado para este escopo.");

            maxIterations = aiSettings.MaxToolCallIterations is >= 1 and <= 10
                ? aiSettings.MaxToolCallIterations : AiChatConstants.DefaultMaxToolCallIterations;

            if (sessionId.HasValue)
            {
                session = await _sessionRepository.GetByIdAsync(sessionId.Value, agentId, ct)
                    ?? throw new ArgumentException($"Sessão {sessionId} não encontrada");
            }
            else
            {
                session = await _sessionRepository.CreateAsync(new AiChatSession
                {
                    Id = Guid.NewGuid(),
                    AgentId = agentId,
                    SiteId = agent.SiteId,
                    ClientId = site.ClientId,
                    Topic = "general",
                    CreatedAt = startTime,
                    CreatedByIp = "unknown",
                    TraceId = traceId,
                    ExpiresAt = startTime.AddDays(AiChatConstants.SessionExpirationDays)
                }, ct);
            }

            var history = await _messageRepository.GetRecentBySessionAsync(session.Id,
                AiChatHelpers.ClampHistoryMessages(aiSettings), ct);
            nextSeq = history.Any() ? history.Max(m => m.SequenceNumber) + 1 : 1;

            (systemPrompt, _) = await _promptBuilder.BuildAsync(agent, session, message, aiSettings, departmentId, ct);

            llmMessages = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);
            if (!string.IsNullOrWhiteSpace(systemNote))
                llmMessages.Add(new LlmMessage("system", systemNote));
            llmMessages.Add(new LlmMessage("user", message));
            setupOk = true;
        }
        catch (Exception ex)
        {
            setupError = ex.Message;
            _logger.LogError(ex, "[{TraceId}] StreamAsync setup falhou para AgentId={AgentId}", traceId, agentId);
        }

        if (!setupOk || session == null || aiSettings == null || systemPrompt == null || llmMessages == null)
        {
            yield return new AiChatStreamChunk(Type: "error", Error: setupError ?? "Erro interno");
            yield break;
        }

        if (!sessionId.HasValue)
        {
            var quickReply = AiChatQuickReply.TryGetReply(message, null);
            if (quickReply != null)
            {
                await _quickReply.PersistAsync(session.Id, message, quickReply, nextSeq, startTime, traceId, aiSettings, stopwatch, ct);
                yield return new AiChatStreamChunk(Type: "token", Content: quickReply);
                yield return new AiChatStreamChunk(Type: "done", SessionId: session.Id, LatencyMs: (int)stopwatch.ElapsedMilliseconds);
                yield break;
            }
        }

        // ── Streaming com tool call loop ──
        var contentBuilder = new StringBuilder();
        var toolIterations = 0;
        int? totalTokens = null;
        var toolMessagesToPersist = new List<AiChatMessage>();
        var executedKbQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consecutiveEmptyKbSearches = 0;
        bool hasToolCalls = false;
        var consecutiveToolErrors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var availableTools = aiSettings.KnowledgeBaseEnabled
            ? await _mcpToolExecutor.GetAvailableToolsAsync(scopeClientId, scopeSiteId, agentId, ct) : [];

        var agentTools = _toolOrchestrator.GetCachedAgentTools(agentId);
        if (agentTools is { Count: > 0 })
        {
            availableTools.AddRange(agentTools);
            _logger.LogDebug("[{TraceId}] StreamAsync: {Count} agent tools mescladas", traceId, agentTools.Count);
        }

        var agentToolCallNames = new HashSet<string>(agentTools?.Select(at => at.Name) ?? [], StringComparer.OrdinalIgnoreCase);
        bool hasAgentToolCallPending = false;
        var agentToolCallsPending = new List<LlmAssistantToolCall>();

        while (true)
        {
            var streamOptions = new LlmOptions(
                MaxTokens: AiChatHelpers.ClampMaxTokens(aiSettings),
                Temperature: AiChatHelpers.ClampTemperature(aiSettings),
                Model: string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                BaseUrl: string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                ApiKey: string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                EnableTools: availableTools.Count > 0, Tools: availableTools,
                Provider: aiSettings.Provider,
                OpenRouterReferer: aiSettings.OpenRouterReferer,
                OpenRouterTitle: aiSettings.OpenRouterTitle,
                OpenRouterCategories: aiSettings.OpenRouterCategories,
                SessionId: session!.Id.ToString("D"),
                TimeoutMs: AiChatHelpers.ClampAiTimeoutMs(aiSettings));

            hasToolCalls = false;
            hasAgentToolCallPending = false;

            if (availableTools.Count > 0)
            {
                await foreach (var evt in _llmProvider.StreamWithToolsAsync(systemPrompt, llmMessages, streamOptions, ct))
                {
                    if (evt.Type == "token" && !string.IsNullOrWhiteSpace(evt.Content))
                    {
                        contentBuilder.Append(evt.Content);
                        yield return new AiChatStreamChunk(Type: "token", Content: evt.Content);
                    }
                    else if (evt.Type == "tool_calls" && evt.ToolCalls is { Count: > 0 })
                    {
                        hasToolCalls = true;
                        totalTokens = evt.TokensUsed;

                        var assistantToolCalls = evt.ToolCalls.Select(tc =>
                            new LlmAssistantToolCall(tc.Id, tc.Name, tc.ArgumentsJson)).ToList();
                        llmMessages.Add(new LlmMessage("assistant", contentBuilder.ToString(), ToolCalls: assistantToolCalls));
                        contentBuilder.Clear();

                        foreach (var toolCall in evt.ToolCalls)
                        {
                            if (agentToolCallNames.Contains(toolCall.Name))
                            {
                                var (isValid, errorJson) = AiChatToolOrchestrator.ValidateAgentToolArguments(toolCall.Name, toolCall.ArgumentsJson);
                                if (!isValid)
                                {
                                    var errCount = consecutiveToolErrors.GetValueOrDefault(toolCall.Name, 0) + 1;
                                    consecutiveToolErrors[toolCall.Name] = errCount;
                                    if (errCount >= 2)
                                    {
                                        contentBuilder.Clear();
                                        contentBuilder.Append("Não foi possível processar sua solicitação automaticamente. Tente reformular sua pergunta ou contate o suporte pelo menu de chamados.");
                                        hasToolCalls = false;
                                        goto streamDone;
                                    }
                                    llmMessages.Add(new LlmMessage("tool", errorJson!, toolCall.Id, toolCall.Name));
                                    continue;
                                }

                                hasAgentToolCallPending = true;
                                agentToolCallsPending.Add(new LlmAssistantToolCall(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson));
                                yield return new AiChatStreamChunk(Type: "tool_call",
                                    ToolCallId: toolCall.Id, ToolName: toolCall.Name, ToolArgumentsDelta: toolCall.ArgumentsJson);
                                continue;
                            }

                            if (toolCall.Name == "knowledge_search")
                            {
                                var kbQuery = AiChatHelpers.ExtractKbQuery(toolCall.ArgumentsJson);
                                if (!string.IsNullOrEmpty(kbQuery) && !executedKbQueries.Add(kbQuery))
                                {
                                    llmMessages.Add(new LlmMessage("tool", """{"found":false,"message":"Busca já realizada sem resultados. Use seu conhecimento próprio."}""", toolCall.Id, toolCall.Name));
                                    continue;
                                }
                            }

                            var toolResult = await _mcpToolExecutor.ExecuteAsync(toolCall.Name, toolCall.ArgumentsJson,
                                scopeClientId, scopeSiteId, agentId, aiSettings, null, departmentId, session.Id, ct);

                            if (toolCall.Name == "knowledge_search" && toolResult.Contains("\"found\":false"))
                                consecutiveEmptyKbSearches++;

                            llmMessages.Add(new LlmMessage("tool", toolResult, toolCall.Id, toolCall.Name));
                            toolMessagesToPersist.Add(new AiChatMessage
                            {
                                Id = Guid.NewGuid(),
                                SessionId = session.Id,
                                SequenceNumber = nextSeq++,
                                Role = "tool",
                                Content = toolResult,
                                ToolCallId = toolCall.Id,
                                ToolName = toolCall.Name,
                                CreatedAt = DateTime.UtcNow,
                                TraceId = traceId
                            });
                        }

                        if (hasAgentToolCallPending)
                        {
                            yield return new AiChatStreamChunk(Type: "round_end", SessionId: session.Id);
                            stopwatch.Stop();
                            try
                            {
                                var msgs = new List<AiChatMessage> { new() { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq++, Role = "user", Content = message, CreatedAt = startTime, TraceId = traceId } };
                                if (agentToolCallsPending.Count > 0)
                                    msgs.Add(new AiChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq++, Role = "assistant", Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : string.Empty, ToolCallsJson = JsonSerializer.Serialize(agentToolCallsPending.Select(tc => new { id = tc.Id, name = tc.Name, arguments = tc.ArgumentsJson })), CreatedAt = DateTime.UtcNow, TraceId = traceId });
                                await _messageRepository.CreateBatchAsync(msgs, ct);
                            }
                            catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir user message do round 1", traceId); }
                            yield break;
                        }
                    }
                    else if (evt.Type == "done") { totalTokens = evt.TokensUsed; }
                }
            }
            else
            {
                await foreach (var token in _llmProvider.StreamAsync(systemPrompt, llmMessages, streamOptions, ct))
                {
                    contentBuilder.Append(token);
                    yield return new AiChatStreamChunk(Type: "token", Content: token);
                }
            }

            if (!hasToolCalls || toolIterations >= maxIterations - 1) break;
            if (consecutiveEmptyKbSearches >= 2) break;
            toolIterations++;
        }

    streamDone:
        stopwatch.Stop();
        var fullContent = contentBuilder.ToString();

        // ── Sanitização de vazamentos de tool calls ──
        // O LLM pode ter emitido tool calls como TEXTO (DSML, blocos ```json
        // com invokes) em vez de function call nativa. Remove antes de seguir.
        var (sanitizedContent, contentWasSanitized) = AiChatLeakSanitizer.Sanitize(fullContent);
        if (contentWasSanitized)
        {
            _logger.LogInformation("[{TraceId}] Vazamentos de tool call removidos do output ({OrigLen} -> {CleanLen} chars)",
                traceId, fullContent.Length, sanitizedContent.Length);
            fullContent = sanitizedContent;
        }

        // ── A2UI: extrai mensagens A2UI do conteúdo do LLM e emite como chunks ──
        // O LLM pode ter gerado interfaces A2UI em blocos ```a2ui. Extraímos e
        // emitimos cada mensagem como um chunk "a2ui" para o agent repassar ao
        // renderer. O texto restante (sem os blocos) segue o fluxo markdown.
        var (cleanContent, a2uiMessages) = AiChatA2uiExtractor.Extract(fullContent);
        fullContent = cleanContent;
        foreach (var a2uiMsg in a2uiMessages)
        {
            yield return new AiChatStreamChunk(Type: "a2ui", A2uiJson: a2uiMsg);
        }

        var shouldTryXmlFallback = availableTools.Count == 0 || !hasToolCalls;
        if (shouldTryXmlFallback)
        {
            var (cleanedContent, updatedNextSeq) = await _toolOrchestrator.ParseAndExecuteXmlToolCallsAsync(
                fullContent, availableTools, scopeClientId, scopeSiteId, agentId,
                aiSettings, departmentId, llmMessages, toolMessagesToPersist,
                session.Id, nextSeq, traceId, ct);
            fullContent = cleanedContent;
            nextSeq = updatedNextSeq;
        }

        if (string.IsNullOrWhiteSpace(fullContent) && toolIterations > 0)
        {
            llmMessages.Add(new LlmMessage("user", "[SISTEMA] Você não forneceu uma resposta visível ao usuário. Forneça uma resposta direta e útil à última pergunta do usuário."));
            var retryOptions = new LlmOptions(
                AiChatHelpers.ClampMaxTokens(aiSettings), AiChatHelpers.ClampTemperature(aiSettings),
                string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                false, null, aiSettings.Provider,
                aiSettings.OpenRouterReferer, aiSettings.OpenRouterTitle, aiSettings.OpenRouterCategories,
                SessionId: session!.Id.ToString("D"),
                TimeoutMs: AiChatHelpers.ClampAiTimeoutMs(aiSettings));
            await foreach (var token in _llmProvider.StreamAsync(systemPrompt!, llmMessages, retryOptions, ct))
            {
                contentBuilder.Append(token);
                yield return new AiChatStreamChunk(Type: "token", Content: token);
            }
            fullContent = contentBuilder.ToString();
            if (string.IsNullOrWhiteSpace(fullContent))
            {
                fullContent = "Não foi possível gerar uma resposta. Tente reformular sua pergunta ou entre em contato com o suporte.";
                yield return new AiChatStreamChunk(Type: "token", Content: fullContent);
            }
        }

        try
        {
            var msgs = new List<AiChatMessage>
            {
                new() { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq++, Role = "user", Content = message, CreatedAt = startTime, TraceId = traceId },
                new() { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq, Role = "assistant", Content = fullContent, TokensUsed = totalTokens, LatencyMs = (int)stopwatch.ElapsedMilliseconds, ModelVersion = aiSettings.ChatModel, CreatedAt = DateTime.UtcNow, TraceId = traceId }
            };
            msgs.AddRange(toolMessagesToPersist);
            await _messageRepository.CreateBatchAsync(msgs, ct);
            _logger.LogInformation("[{TraceId}] StreamAsync concluído: AgentId={AgentId}, ContentLen={Len}, Latency={LatencyMs}ms", traceId, agentId, fullContent.Length, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) { _logger.LogError(ex, "[{TraceId}] Falha ao persistir mensagens do stream", traceId); }

        yield return new AiChatStreamChunk(Type: "done", SessionId: session.Id, LatencyMs: (int)stopwatch.ElapsedMilliseconds);
    }

    public async IAsyncEnumerable<AiChatStreamChunk> StreamMultiRoundAsync(
        Guid agentId, string? message, Guid? sessionId,
        List<ToolResultItem>? toolResults,
        Func<Guid, CancellationToken, Task<AIIntegrationSettings>> resolveAiSettings,
        Guid? departmentId = null,
        string? systemNote = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var stopwatch = Stopwatch.StartNew();

        if (toolResults is not { Count: > 0 } && string.IsNullOrWhiteSpace(message))
        {
            yield return new AiChatStreamChunk(Type: "error", Error: "ToolResults ou Message requeridos."); yield break;
        }
        if (!sessionId.HasValue)
        {
            yield return new AiChatStreamChunk(Type: "error", Error: "SessionId requerido em multi-round."); yield break;
        }

        var session = await _sessionRepository.GetByIdAsync(sessionId.Value, agentId, ct);
        if (session == null) { yield return new AiChatStreamChunk(Type: "error", Error: $"Sessão {sessionId} não encontrada."); yield break; }

        var aiSettings = await resolveAiSettings(session.SiteId, ct);
        if (!aiSettings.Enabled || !aiSettings.ChatAIEnabled) { yield return new AiChatStreamChunk(Type: "error", Error: "Chat IA desabilitado."); yield break; }

        var history = await _messageRepository.GetRecentBySessionAsync(session.Id, AiChatHelpers.ClampHistoryMessages(aiSettings), ct);
        var nextSeq = history.Any() ? history.Max(m => m.SequenceNumber) + 1 : 1;

        var llmMessages = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);

        if (!string.IsNullOrWhiteSpace(systemNote))
            llmMessages.Add(new LlmMessage("system", systemNote));

        if (!string.IsNullOrWhiteSpace(message))
        {
            try
            {
                await _messageRepository.CreateAsync(new AiChatMessage
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    SequenceNumber = nextSeq++,
                    Role = "user",
                    Content = message,
                    CreatedAt = DateTime.UtcNow,
                    TraceId = traceId
                }, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir mensagem do usuário no multi-round", traceId); }
        }

        if (toolResults is { Count: > 0 })
        {
            var toolMsgs = new List<AiChatMessage>();
            foreach (var tr in toolResults)
            {
                var wrapped = AiChatToolOrchestrator.WrapAgentToolError(tr.Result, tr.Name);
                llmMessages.Add(new LlmMessage("tool", wrapped, $"agent_{tr.CallId}", tr.Name));
                toolMsgs.Add(new AiChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq++, Role = "tool", Content = wrapped, ToolCallId = $"agent_{tr.CallId}", ToolName = tr.Name, CreatedAt = DateTime.UtcNow, TraceId = traceId });
            }
            try { await _messageRepository.CreateBatchAsync(toolMsgs, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir tool results", traceId); }
        }

        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null) { yield return new AiChatStreamChunk(Type: "error", Error: "Agent não encontrado."); yield break; }

        var (systemPrompt, _) = await _promptBuilder.BuildAsync(agent, session,
            message ?? toolResults?.FirstOrDefault()?.Result ?? "", aiSettings, departmentId, ct);

        var availableTools = aiSettings.KnowledgeBaseEnabled
            ? await _mcpToolExecutor.GetAvailableToolsAsync(session.ClientId, session.SiteId, agentId, ct)
            : new List<LlmTool>();
        var agentTools = _toolOrchestrator.GetCachedAgentTools(agentId);
        if (agentTools is { Count: > 0 }) availableTools.AddRange(agentTools);

        var maxIterations = aiSettings.MaxToolCallIterations is >= 1 and <= 10
            ? aiSettings.MaxToolCallIterations : AiChatConstants.DefaultMaxToolCallIterations;

        var contentBuilder = new StringBuilder();
        var toolIterations = 0;
        int? totalTokens = null;
        var executedKbQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consecutiveEmptyKbSearches = 0;
        bool hasToolCalls = false;
        var agentToolCallNames = new HashSet<string>(agentTools?.Select(at => at.Name) ?? [], StringComparer.OrdinalIgnoreCase);
        var consecutiveToolErrors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var streamOptions = new LlmOptions(
                AiChatHelpers.ClampMaxTokens(aiSettings), AiChatHelpers.ClampTemperature(aiSettings),
                string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                availableTools.Count > 0, availableTools, aiSettings.Provider,
                aiSettings.OpenRouterReferer, aiSettings.OpenRouterTitle, aiSettings.OpenRouterCategories,
                SessionId: session.Id.ToString("D"),
                TimeoutMs: AiChatHelpers.ClampAiTimeoutMs(aiSettings));

            hasToolCalls = false;
            bool hasAgentToolCall = false;

            await foreach (var evt in _llmProvider.StreamWithToolsAsync(systemPrompt, llmMessages, streamOptions, ct))
            {
                if (evt.Type == "token" && !string.IsNullOrWhiteSpace(evt.Content))
                {
                    contentBuilder.Append(evt.Content);
                    yield return new AiChatStreamChunk(Type: "token", Content: evt.Content);
                }
                else if (evt.Type == "tool_calls" && evt.ToolCalls is { Count: > 0 })
                {
                    hasToolCalls = true;
                    totalTokens = evt.TokensUsed;
                    var assistantToolCalls = evt.ToolCalls.Select(tc =>
                        new LlmAssistantToolCall(tc.Id, tc.Name, tc.ArgumentsJson)).ToList();
                    llmMessages.Add(new LlmMessage("assistant", contentBuilder.ToString(), ToolCalls: assistantToolCalls));
                    contentBuilder.Clear();

                    foreach (var tc in evt.ToolCalls)
                    {
                        if (agentToolCallNames.Contains(tc.Name))
                        {
                            var (isValid, errorJson) = AiChatToolOrchestrator.ValidateAgentToolArguments(tc.Name, tc.ArgumentsJson);
                            if (!isValid)
                            {
                                var errCount = consecutiveToolErrors.GetValueOrDefault(tc.Name, 0) + 1;
                                consecutiveToolErrors[tc.Name] = errCount;
                                if (errCount >= 2) { contentBuilder.Append("Não foi possível processar sua solicitação automaticamente. Tente reformular sua pergunta ou contate o suporte pelo menu de chamados."); hasToolCalls = false; goto streamMultiRoundDone; }
                                llmMessages.Add(new LlmMessage("tool", errorJson!, tc.Id, tc.Name));
                                continue;
                            }
                            hasAgentToolCall = true;
                            yield return new AiChatStreamChunk(Type: "tool_call", ToolCallId: tc.Id, ToolName: tc.Name, ToolArgumentsDelta: tc.ArgumentsJson);
                        }
                        else
                        {
                            if (tc.Name == "knowledge_search")
                            {
                                var kbQuery = AiChatHelpers.ExtractKbQuery(tc.ArgumentsJson);
                                if (!string.IsNullOrEmpty(kbQuery) && !executedKbQueries.Add(kbQuery)) continue;
                            }
                            var toolResult = await _mcpToolExecutor.ExecuteAsync(tc.Name, tc.ArgumentsJson,
                                session.ClientId, session.SiteId, agentId, aiSettings, null, departmentId, session.Id, ct);
                            yield return new AiChatStreamChunk(Type: "tool_result", ToolCallId: tc.Id, ToolResult: toolResult);
                            llmMessages.Add(new LlmMessage("tool", toolResult, tc.Id, tc.Name));
                            if (tc.Name == "knowledge_search" && toolResult.Contains("\"found\":false")) consecutiveEmptyKbSearches++;
                        }
                    }

                    if (hasAgentToolCall)
                    {
                        try
                        {
                            await _messageRepository.CreateAsync(new AiChatMessage
                            {
                                Id = Guid.NewGuid(),
                                SessionId = session.Id,
                                SequenceNumber = nextSeq,
                                Role = "assistant",
                                Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : string.Empty,
                                ToolCallsJson = JsonSerializer.Serialize(assistantToolCalls.Select(tc => new { id = tc.Id, name = tc.Name, arguments = tc.ArgumentsJson })),
                                CreatedAt = DateTime.UtcNow,
                                TraceId = traceId
                            }, ct);
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir assistant no multi-round", traceId); }
                        yield return new AiChatStreamChunk(Type: "round_end", SessionId: session.Id);
                        yield break;
                    }
                }
                else if (evt.Type == "done") { totalTokens = evt.TokensUsed; }
            }

            if (!hasToolCalls || toolIterations >= maxIterations - 1 || consecutiveEmptyKbSearches >= 2) break;
            toolIterations++;
        }

    streamMultiRoundDone:
        stopwatch.Stop();
        var fullContent = contentBuilder.ToString();
        if (string.IsNullOrWhiteSpace(fullContent)) fullContent = "Não foi possível gerar uma resposta. Tente reformular sua pergunta.";

        // ── Sanitização de vazamentos de tool calls ──
        var (sanitizedMultiContent, multiWasSanitized) = AiChatLeakSanitizer.Sanitize(fullContent);
        if (multiWasSanitized)
        {
            _logger.LogInformation("[{TraceId}] Vazamentos de tool call removidos do output multi-round ({OrigLen} -> {CleanLen} chars)",
                traceId, fullContent.Length, sanitizedMultiContent.Length);
            fullContent = sanitizedMultiContent;
        }

        // ── A2UI: extrai mensagens A2UI do conteúdo do LLM e emite como chunks ──
        var (cleanMultiContent, a2uiMultiMessages) = AiChatA2uiExtractor.Extract(fullContent);
        fullContent = cleanMultiContent;
        foreach (var a2uiMsg in a2uiMultiMessages)
        {
            yield return new AiChatStreamChunk(Type: "a2ui", A2uiJson: a2uiMsg);
        }

        try
        {
            await _messageRepository.CreateAsync(new AiChatMessage
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                SequenceNumber = nextSeq,
                Role = "assistant",
                Content = fullContent,
                TokensUsed = totalTokens,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                ModelVersion = aiSettings.ChatModel,
                CreatedAt = DateTime.UtcNow,
                TraceId = traceId
            }, ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "[{TraceId}] Erro ao persistir multi-round", traceId); }

        yield return new AiChatStreamChunk(Type: "done", SessionId: session.Id,
            TokensUsed: totalTokens, LatencyMs: (int)stopwatch.ElapsedMilliseconds);
    }
}

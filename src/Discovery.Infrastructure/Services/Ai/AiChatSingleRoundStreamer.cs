using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AiChatSingleRoundStreamer
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
    private readonly IMemoryCache _memoryCache;
    private readonly AiChatCapabilityResolver _capabilities;
    private readonly AiChatToolRateLimiter _rateLimiter;
    private readonly IAiCostControlService? _costControl;

    public AiChatSingleRoundStreamer(
        IAiChatSessionRepository sessionRepository,
        IAiChatMessageRepository messageRepository,
        IAgentRepository agentRepository,
        ISiteRepository siteRepository,
        ILlmProvider llmProvider,
        IMcpToolExecutor mcpToolExecutor,
        ILogger<AiChatService> logger,
        AiChatSystemPromptBuilder promptBuilder,
        AiChatToolOrchestrator toolOrchestrator,
        AiChatQuickReply quickReply,
        IMemoryCache memoryCache,
        AiChatCapabilityResolver capabilities,
        AiChatToolRateLimiter rateLimiter,
        IAiCostControlService? costControl = null)
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
        _memoryCache = memoryCache;
        _capabilities = capabilities;
        _rateLimiter = rateLimiter;
        _costControl = costControl;
    }

    public async IAsyncEnumerable<AiChatStreamChunk> StreamAsync(
        Guid agentId, string message, Guid? sessionId,
        Func<Guid, CancellationToken, Task<AIIntegrationSettings>> resolveAiSettings,
        Guid? departmentId = null,
        string? systemNote = null,
        List<string>? images = null,
        [EnumeratorCancellation] CancellationToken ct = default,
        int maxImageBase64Chars = 0)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var startTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        AiChatSession? session = null;
        string? systemPrompt = null;
        List<LlmMessage>? llmMessages = null;
        List<AiChatMessage>? history = null; // B15: visivel fora do try (quick-reply)
        int nextSeq = 1;
        bool setupOk = false;
        string? setupError = null;
        // Imagens do turno: usados no fallback quando o provedor recusa o payload
        // multimodal (repete UMA vez sem imagens) e para evitar loop.
        bool hadImagesForTurn = false;
        bool retriedWithoutImages = false;
        AIIntegrationSettings? aiSettings = null;
        Guid scopeClientId = Guid.Empty;
        Guid scopeSiteId = Guid.Empty;
        int maxIterations = AiChatConstants.DefaultMaxToolCallIterations;
        int maxTokens = AiChatConstants.DefaultMaxTokens;

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

            if (!aiSettings.Enabled)
                throw new InvalidOperationException("A Integração com IA está desabilitada neste servidor.");
            if (!aiSettings.ChatAIEnabled)
                throw new InvalidOperationException("O Chat IA está desabilitado para este escopo (ligue em Funcionalidades do Sistema > Chat IA).");

            maxIterations = AiChatHelpers.ResolveMaxToolIterations(aiSettings);
            maxTokens = await _capabilities.ResolveChatMaxTokensAsync(scopeSiteId, aiSettings, ct);

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

            var historyLimit = AiChatHelpers.ClampHistoryMessages(aiSettings);
            history = await _messageRepository.GetRecentBySessionAsync(session.Id, historyLimit + 12, ct);
            nextSeq = history.Any() ? history.Max(m => m.SequenceNumber) + 1 : 1;

            (systemPrompt, _) = await _promptBuilder.BuildAsync(agent, session, message, aiSettings, departmentId, ct,
                await _capabilities.IsA2uiEnabledAsync(session.ClientId, session.SiteId, agentId, ct));

            llmMessages = AiChatHelpers.BuildCompactedLlmMessages(history, historyLimit);
            if (!string.IsNullOrWhiteSpace(systemNote))
                llmMessages.Add(new LlmMessage("system", systemNote));
            // Guard de visão: só envia a imagem se o modelo aceitar (setting +
            // capacidade declarada no catálogo). Sem visão, o LLM recebe a
            // mensagem de texto e uma nota para avisar o usuário.
            var sendScreenshotImages = images is { Count: > 0 } &&
                await _capabilities.ShouldSendScreenshotImagesAsync(aiSettings, scopeClientId, scopeSiteId, ct);
            hadImagesForTurn = sendScreenshotImages && images is { Count: > 0 };
            if (images is { Count: > 0 } && !sendScreenshotImages)
            {
                llmMessages.Add(new LlmMessage("system",
                    "[SISTEMA] O usuário anexou uma imagem, mas o modelo configurado não tem visão (ou o envio de prints está desabilitado). " +
                    "Avise o usuário que a imagem não pôde ser analisada e peça uma descrição em texto do que aparece na tela."));
            }
            // A nota de imagens omitidas vem ANTES da mensagem do usuário para o
            // modelo já saber que o anexo não está completo.
            var userMessageWithImages = AiChatCapabilityResolver.BuildUserMessageWithImages(
                message, sendScreenshotImages ? images : null, _logger, out var omittedImages,
                maxImageBase64Chars);
            if (omittedImages > 0)
            {
                llmMessages.Add(new LlmMessage("system",
                    $"[SISTEMA] {omittedImages} imagem(ns) anexada(s) não puderam ser enviadas (tamanho acima do orçamento do turno). " +
                    "Avise o usuário e peça que envie menos imagens por vez ou descreva o conteúdo em texto."));
            }
            llmMessages.Add(userMessageWithImages);
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

        // Quick-reply: saudações e mensagens triviais curtas são respondidas
        // via cache sem chamada ao LLM — reduz latência e custo. Aplica-se
        // tanto à primeira mensagem (sem sessão) quanto a saudações puras no
        // meio da conversa (ex.: usuário manda "oi" de novo) — o matcher só
        // responde mensagens triviais; mensagens com contexto real vão ao LLM.
        // B15: passa o histórico real — "oi" no meio de uma conversa técnica
        // não deve receber a saudação em cache.
        // Com imagens anexadas, NUNCA usar quick-reply: a resposta em cache não
        // olha a imagem e o usuário receberia algo genérico.
        var quickReplyMatch = images is { Count: > 0 } ? null : AiChatQuickReply.TryGetReply(message, history: history);
        if (quickReplyMatch != null)
        {
            await _quickReply.PersistAsync(session.Id, message, quickReplyMatch, nextSeq, startTime, traceId, aiSettings, stopwatch, ct);
            yield return new AiChatStreamChunk(Type: "token", Content: quickReplyMatch);
            yield return new AiChatStreamChunk(Type: "done", SessionId: session.Id, LatencyMs: (int)stopwatch.ElapsedMilliseconds);
            yield break;
        }

        var costAcquired = false;
        if (_costControl is not null)
        {
            costAcquired = await _costControl.TryAcquireAsync(scopeClientId, scopeSiteId, aiSettings, ct);
            if (!costAcquired)
            {
                yield return new AiChatStreamChunk(Type: "error", Error: "Limite de uso ou orçamento de IA excedido para este escopo.");
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
        // B16: rastreia se algum token visível já foi transmitido ao cliente.
        // Garante que NENHUM turno 200 termine com conteúdo persistido e zero
        // tokens (bug de produção: fallback injetado no contentBuilder não era
        // emitido, o agent exibia a mensagem genérica e o servidor persistia
        // uma resposta que o usuário nunca viu).
        bool anyTokenYielded = false;
        // B16-r2: erro estruturado do provider (objeto "error" no stream do LLM).
        string? providerError = null;

        var kbTools = aiSettings.KnowledgeBaseEnabled
            ? await _mcpToolExecutor.GetAvailableToolsAsync(scopeClientId, scopeSiteId, agentId, ct) : [];

        var agentTools = await _toolOrchestrator.GetAgentToolsForScopeAsync(agentId, scopeClientId, scopeSiteId, ct);
        // Dedupe por nome: KB + agent podem colidir e função repetida no payload do
        // provedor é ambígua (a OpenAI recusa).
        var availableTools = AiChatToolOrchestrator.ApplyToolPromptOverrides(
            AiChatToolOrchestrator.MergeDistinctTools(kbTools, agentTools),
            aiSettings.ToolPromptOverrides);
        if (availableTools.Count > 0 && !await _capabilities.ShouldSendToolsAsync(aiSettings, scopeClientId, scopeSiteId, ct))
        {
            _logger.LogInformation("[{TraceId}] Modelo {Model} não suporta tools no catálogo — omitindo {Count} tools no round 1.",
                traceId, aiSettings.ChatModel, availableTools.Count);
            availableTools.Clear();
        }
        if (agentTools is { Count: > 0 })
        {
            _logger.LogDebug("[{TraceId}] StreamAsync: {Count} agent tools mescladas ({Total} no total após dedupe)",
                traceId, agentTools.Count, availableTools.Count);
        }

        var agentToolCallNames = new HashSet<string>(agentTools?.Select(at => at.Name) ?? [], StringComparer.OrdinalIgnoreCase);
        // Governança: políticas efetivas (enable/disable, rate limit e timeout).
        var agentToolPolicies = await _toolOrchestrator.GetEffectivePoliciesAsync(scopeClientId, scopeSiteId, agentId, ct);
        var agentToolPolicyMap = agentToolPolicies.ToDictionary(p => p.ToolName, StringComparer.OrdinalIgnoreCase);
        var agentToolTimeouts = AiChatToolRateLimiter.BuildToolTimeouts(agentToolPolicies, agentToolCallNames);
        // B17: schema registrado por tool (validação de argumentos agnóstica de modelo).
        // GroupBy evita exceção de chave duplicada se o agent registrar nomes repetidos.
        var agentToolSchemas = agentTools?.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Schema, StringComparer.OrdinalIgnoreCase);
        bool hasAgentToolCallPending = false;
        var agentToolCallsPending = new List<LlmAssistantToolCall>();
        bool kbExhausted = false;
        // Round do TURNO (não do request). Em StreamAsync é sempre o 1º round:
        // as continuações de tool chain do agent chegam por StreamMultiRoundAsync,
        // que lê esta chave da cache para continuar a contagem. Antes, o contador
        // local (toolIterations) zerava a cada request e o heartbeat mostrava
        // sempre "round 1" em tool chains MCP.
        var turnRound = AiChatTurnRound.Resolve(_memoryCache, session.Id, hasToolResults: false);
        // Quando o orçamento esgota antes de delegar a um agent, o round seguinte
        // roda SEM tools para forçar a resposta final (não dá para confiar que o
        // modelo deixará de pedir tools só com uma nota de sistema).
        bool budgetExhausted = false;

        while (true)
        {
            // Heartbeat de progresso do agent loop — agent/frontend exibem "round X/Y".
            // Só é emitido quando há tools (sem tools o loop é single-pass e o
            // heartbeat seria enganoso). No round de síntese (budgetExhausted) o
            // heartbeat é omitido de propósito: não há mais loop de tools.
            if (availableTools.Count > 0 && !budgetExhausted)
                yield return new AiChatStreamChunk(Type: "loop_progress", LoopRound: turnRound, LoopMaxRounds: maxIterations);

            // budgetExhausted: nenhuma tool é oferecida no round de síntese — se
            // ainda fossem oferecidas, o modelo poderia driblar o orçamento.
            // KB esgotada: remove knowledge_search das tools disponíveis deste round
            // (mais determinístico que confiar apenas na nota de sistema).
            var roundTools = budgetExhausted
                ? new List<LlmTool>()
                : kbExhausted
                    ? availableTools.Where(t => t.Name != "knowledge_search").ToList()
                    : availableTools;
            roundTools = AiChatHelpers.CapToolsPerRound(roundTools);

            var streamOptions = new LlmOptions(
                MaxTokens: maxTokens,
                Temperature: AiChatHelpers.ClampTemperature(aiSettings),
                Model: string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                BaseUrl: string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                ApiKey: string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                EnableTools: roundTools.Count > 0, Tools: roundTools,
                Provider: aiSettings.Provider,
                OpenRouterReferer: aiSettings.OpenRouterReferer,
                OpenRouterTitle: aiSettings.OpenRouterTitle,
                OpenRouterCategories: aiSettings.OpenRouterCategories,
                SessionId: session!.Id.ToString("D"),
                TimeoutMs: AiChatHelpers.ClampAiTimeoutMs(aiSettings),
                // A7: amostragem configurável
                TopP: AiChatHelpers.ClampTopP(aiSettings),
                FrequencyPenalty: AiChatHelpers.ClampPenalty(aiSettings.FrequencyPenalty),
                PresencePenalty: AiChatHelpers.ClampPenalty(aiSettings.PresencePenalty),
                Seed: aiSettings.Seed,
                ResponseFormat: aiSettings.ResponseFormat,
                ReasoningEnabled: aiSettings.ReasoningEnabled, ReasoningEffort: aiSettings.ReasoningEffort);

            hasToolCalls = false;
            hasAgentToolCallPending = false;
            providerError = null;

            var streamFilter = new AiChatStreamFilter(aiSettings);
            if (roundTools.Count > 0)
            {
                await foreach (var evt in _llmProvider.StreamWithToolsAsync(systemPrompt, llmMessages, streamOptions, ct))
                {
                    if (evt.Type == "error")
                    {
                        providerError = evt.Content ?? "Erro no stream do provider";
                        break;
                    }
                    if (AiChatStreamingOrchestrator.IsTextToken(evt))
                    {
                        contentBuilder.Append(evt.Content);
                        var safeToken = streamFilter.Push(evt.Content);
                        if (!string.IsNullOrEmpty(safeToken))
                        {
                            yield return new AiChatStreamChunk(Type: "token", Content: safeToken);
                            anyTokenYielded = true;
                        }
                    }
                    else if (evt.Type == "tool_calls" && evt.ToolCalls is { Count: > 0 })
                    {
                        hasToolCalls = true;
                        if (evt.TokensUsed is > 0)
                            totalTokens = (totalTokens ?? 0) + evt.TokensUsed.Value;

                        var roundPreamble = contentBuilder.ToString();
                        var assistantToolCalls = evt.ToolCalls.Select(tc =>
                            new LlmAssistantToolCall(tc.Id, tc.Name, tc.ArgumentsJson)).ToList();
                        llmMessages.Add(new LlmMessage("assistant", roundPreamble, ToolCalls: assistantToolCalls));
                        contentBuilder.Clear();

                        foreach (var toolCall in evt.ToolCalls)
                        {
                            if (agentToolCallNames.Contains(toolCall.Name))
                            {
                                var (isValid, errorJson) = AiChatToolOrchestrator.ValidateAgentToolArguments(toolCall.Name, toolCall.ArgumentsJson, agentToolSchemas?.GetValueOrDefault(toolCall.Name));
                                if (!isValid)
                                {
                                    var errCount = consecutiveToolErrors.GetValueOrDefault(toolCall.Name, 0) + 1;
                                    consecutiveToolErrors[toolCall.Name] = errCount;
                                    _logger.LogWarning("[{TraceId}] Argumentos rejeitados para tool do agent {ToolName} (tentativa {Count}): {Error} Args={Args}",
                                        traceId, toolCall.Name, errCount, errorJson, AiChatCapabilityResolver.Shorten(toolCall.ArgumentsJson, 300));
                                    if (errCount >= AiChatToolRateLimiter.MaxConsecutiveAgentToolArgErrors)
                                    {
                                        // B16: NÃO injetar fallback direto no contentBuilder (nunca
                                        // era transmitido ao cliente). Limpa o buffer e encerra o loop
                                        // de tools; a síntese forçada em streamDone tenta uma resposta
                                        // real e, se falhar, o fallback é emitido como token.
                                        _logger.LogWarning("[{TraceId}] Tool do agent {ToolName} rejeitada {Count}x por argumentos inválidos. Args={Args}. Encerrando loop de tools e sintetizando resposta.",
                                            traceId, toolCall.Name, errCount, AiChatCapabilityResolver.Shorten(toolCall.ArgumentsJson, 300));
                                        contentBuilder.Clear();
                                        hasToolCalls = false;
                                        goto streamDone;
                                    }
                                    llmMessages.Add(new LlmMessage("tool", errorJson!, toolCall.Id, toolCall.Name));
                                    continue;
                                }

                                var rateError = await _rateLimiter.CheckAgentToolRateLimitAsync(
                                    toolCall.Name, new McpToolScope(scopeClientId, scopeSiteId, agentId), agentToolPolicyMap);
                                if (rateError is not null)
                                {
                                    llmMessages.Add(new LlmMessage("tool", rateError, toolCall.Id, toolCall.Name));
                                    continue;
                                }

                                hasAgentToolCallPending = true;
                                agentToolCallsPending.Add(new LlmAssistantToolCall(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson));
                                // Orçamento esgotado: NÃO emite o tool_call ao client. O
                                // bloco pós-foreach troca a delegação por nota de síntese
                                // e reinicia o round sem tools (ver budgetExhausted).
                                if (!AiChatTurnRound.IsBudgetExhausted(turnRound, maxIterations))
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

                            // KB esgotada: nota de sistema + remove a tool no próximo round
                            // (em vez de break silencioso que cortava a resposta).
                            if (consecutiveEmptyKbSearches >= 2 && !kbExhausted)
                            {
                                kbExhausted = true;
                                llmMessages.Add(new LlmMessage("system", AiChatHelpers.KbExhaustedNote));
                            }

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
                            // Orçamento do turno esgotado: o request iria delegar a tool
                            // ao agent, mas o admin já configurou (ex.) 3 rounds e este é
                            // o 3º. Antes o yield break abaixo ignorava MaxToolCallIterations
                            // e quem limitava era o agent (20 rounds/10min). Aqui não se
                            // emite tool_call/round_end nem se persiste round pendente:
                            // injeta a nota de síntese e reinicia o round sem tools.
                            if (AiChatTurnRound.IsBudgetExhausted(turnRound, maxIterations))
                            {
                                budgetExhausted = true;
                                hasAgentToolCallPending = false;
                                // A mensagem assistant com tool_calls já entrou em
                                // llmMessages; como NÃO delegamos ao agent, fecha cada
                                // chamada com um tool message sintético — sem isso o
                                // round de síntese sem tools recebe 400 (tool_call sem
                                // resposta) do provedor OpenAI-compatible.
                                foreach (var pendingCall in agentToolCallsPending)
                                    llmMessages.Add(new LlmMessage("tool", AiChatHelpers.AgentToolBudgetExhaustedResult, pendingCall.Id, pendingCall.Name));
                                agentToolCallsPending.Clear();
                                llmMessages.Add(new LlmMessage("system", AiChatHelpers.SynthesisBudgetNote));
                                _logger.LogInformation(
                                    "[{TraceId}] Orçamento de rounds do turno esgotado ({Round}/{Max}); sintetizando resposta sem tools em vez de delegar ao agent",
                                    traceId, turnRound, maxIterations);
                                // Sai do await foreach; o while reentra sem tools (budgetExhausted).
                                break;
                            }
                            // Registra round pendente para o watchdog (mesmo mecanismo
                            // do multi-round): se o agent não devolver ToolResults
                            // dentro do TTL, o próximo multi-round desta sessão
                            // injeta nota de expiração.
                            _memoryCache.Set($"pending_round:{session.Id}", DateTime.UtcNow, AiChatConstants.PendingRoundTtl);
                            stopwatch.Stop();
                            // B3: persistir ANTES de emitir round_end — se o client
                            // cair logo após receber round_end, o enumerator é
                            // descartado e as mensagens nunca seriam persistidas.
                            try
                            {
                                var msgs = new List<AiChatMessage> { new() { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq++, Role = "user", Content = message, CreatedAt = startTime, TraceId = traceId } };
                                if (agentToolCallsPending.Count > 0)
                                    msgs.Add(new AiChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq++, Role = "assistant", Content = roundPreamble, ToolCallsJson = JsonSerializer.Serialize(agentToolCallsPending.Select(tc => new { id = tc.Id, name = tc.Name, arguments = tc.ArgumentsJson })), CreatedAt = DateTime.UtcNow, TraceId = traceId });
                                await _messageRepository.CreateBatchAsync(msgs, ct);
                            }
                            catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir user message do round 1", traceId); }
                            yield return new AiChatStreamChunk(Type: "round_end", SessionId: session.Id,
                                ToolTimeouts: agentToolTimeouts.Count > 0 ? agentToolTimeouts : null);
                            yield break;
                        }
                    }
                    else if (evt.Type == "done")
                    {
                        if (evt.TokensUsed is > 0)
                            totalTokens = (totalTokens ?? 0) + evt.TokensUsed.Value;
                    }
                }
                var tailSafe = streamFilter.Flush();
                if (!string.IsNullOrEmpty(tailSafe))
                {
                    yield return new AiChatStreamChunk(Type: "token", Content: tailSafe);
                    anyTokenYielded = true;
                }
            }
            else
            {
                await foreach (var token in _llmProvider.StreamAsync(systemPrompt, llmMessages, streamOptions, ct))
                {
                    contentBuilder.Append(token);
                    var safeToken = streamFilter.Push(token);
                    if (!string.IsNullOrEmpty(safeToken))
                    {
                        yield return new AiChatStreamChunk(Type: "token", Content: safeToken);
                        anyTokenYielded = true;
                    }
                }
                var tailSafe = streamFilter.Flush();
                if (!string.IsNullOrEmpty(tailSafe))
                {
                    yield return new AiChatStreamChunk(Type: "token", Content: tailSafe);
                    anyTokenYielded = true;
                }
            }

            // B16-r2: erro estruturado do provider (objeto "error" no stream) —
            // encerra o turno com mensagem clara em vez de um done vazio.
            if (providerError is not null)
            {
                // FALLBACK multimodal: provedor recusou o payload com imagens antes
                // de emitir qualquer token. Repete UMA única vez o while SEM imagens
                // (mensagem user vira texto puro + nota de sistema). Sem essa saída o
                // turno inteiro morria só porque o modelo/provedor não aceita visão.
                if (hadImagesForTurn && !anyTokenYielded && !retriedWithoutImages)
                {
                    retriedWithoutImages = true;
                    _logger.LogWarning(
                        "[{TraceId}] Provider recusou as imagens ({Error}); repetindo o turno UMA vez sem imagens.",
                        traceId, providerError);

                    // Substitui a última mensagem user (a que carrega ContentParts).
                    for (var i = llmMessages.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(llmMessages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                        {
                            llmMessages[i] = new LlmMessage("user", message);
                            break;
                        }
                    }
                    llmMessages.Add(new LlmMessage("system",
                        "[SISTEMA] As imagens não puderam ser processadas pelo provedor; responda com base no texto e peça uma descrição do que aparece na imagem."));
                    providerError = null;
                    contentBuilder.Clear();
                    hasToolCalls = false;
                    continue;
                }

                if (_costControl is not null && costAcquired && !anyTokenYielded && !hasToolCalls)
                {
                    try { await _costControl.ReleaseAsync(scopeClientId, scopeSiteId, aiSettings, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao liberar slot no CostControl", traceId); }
                }
                _logger.LogError("[{TraceId}] Provider stream error: {Error}", traceId, providerError);
                yield return new AiChatStreamChunk(Type: "error", Error: providerError);
                yield break;
            }

            // budgetExhausted: garante que o round sem tools aconteça mesmo que o
            // orçamento local (toolIterations) já tenha sido atingido; o break
            // natural ocorre depois, quando o round sem tools não pede tool.
            if (!hasToolCalls || (!budgetExhausted && toolIterations >= maxIterations)) break;
            toolIterations++;
        }

    streamDone:
        stopwatch.Stop();
        var fullContent = contentBuilder.ToString();

        if (_costControl is not null)
        {
            if (totalTokens is > 0)
            {
                try { await _costControl.RecordUsageAsync(scopeClientId, scopeSiteId, totalTokens.Value, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao registrar uso de tokens no CostControl", traceId); }
            }
            else if (costAcquired && !string.IsNullOrEmpty(providerError) && !anyTokenYielded && !hasToolCalls)
            {
                try { await _costControl.ReleaseAsync(scopeClientId, scopeSiteId, aiSettings, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao liberar slot no CostControl", traceId); }
            }
        }

        fullContent = AiChatGuardrails.ApplyOutputGuardrails(fullContent, aiSettings);

        // ── A2UI + sanitização de vazamentos (ordem única, ver AiChatOutputPipeline) ──
        // A extração A2UI DEVE rodar antes da sanitização: o sanitizador apaga
        // blocos ```a2ui como fallback e, se rodasse primeiro, o agent nunca
        // receberia o chunk "a2ui" (a interface sumia do chat).
        var (cleanContent, a2uiMessages, contentWasSanitized) = AiChatOutputPipeline.Process(
            fullContent,
            reason => _logger.LogWarning("[{TraceId}] A2UI — {Reason}", traceId, reason));
        if (contentWasSanitized)
        {
            _logger.LogInformation("[{TraceId}] Vazamentos de tool call removidos do output ({OrigLen} -> {CleanLen} chars)",
                traceId, fullContent.Length, cleanContent.Length);
        }
        fullContent = cleanContent;
        var a2uiEnabled = await _capabilities.IsA2uiEnabledAsync(session.ClientId, session.SiteId, agentId, ct);
        if (!a2uiEnabled)
        {
            // Capacidade desligada na governança: o bloco já saiu do texto
            // visível; aqui o chunk é descartado (defesa em profundidade).
            if (a2uiMessages.Count > 0)
                _logger.LogWarning("[{TraceId}] A2UI desabilitada na governanca: {Count} mensagem(ns) descartada(s)", traceId, a2uiMessages.Count);
        }
        else
        {
            // M2 (revisão 2026-10-08): createSurface sem a definição completa
            // (updateComponents com "root") deixa a bolha em "Loading surface...".
            // Em vez de encerrar o turno só com o aviso, pede UMA reemissão ao LLM
            // e, se ela vier válida, entrega o card de verdade.
            var missingDefinitions = AiChatA2uiExtractor.SurfacesMissingDefinition(a2uiMessages);
            if (missingDefinitions.Count > 0)
            {
                _logger.LogWarning("[{TraceId}] A2UI sem definição para a(s) surface(s) {Surfaces} — solicitando reemissão ao LLM",
                    traceId, string.Join(", ", missingDefinitions));
                var (retryContent, retryTokens) = await _capabilities.TryReemitA2uiDefinitionAsync(
                    systemPrompt!, llmMessages, aiSettings, maxTokens, session.Id,
                    contentBuilder.ToString(), missingDefinitions, ct);
                if (retryTokens > 0)
                    totalTokens = (totalTokens ?? 0) + retryTokens;
                if (!string.IsNullOrWhiteSpace(retryContent))
                {
                    var (retryClean, retryMessages, retrySanitized) = AiChatOutputPipeline.Process(
                        retryContent,
                        reason => _logger.LogWarning("[{TraceId}] A2UI (reemissão) — {Reason}", traceId, reason));
                    if (retrySanitized)
                    {
                        _logger.LogInformation("[{TraceId}] Vazamentos de tool call removidos da reemissão A2UI ({OrigLen} -> {CleanLen} chars)",
                            traceId, retryContent.Length, retryClean.Length);
                    }
                    // Só substitui quando a reemissão realmente trouxe a definição.
                    if (retryMessages.Count > 0 && AiChatA2uiExtractor.SurfacesMissingDefinition(retryMessages).Count == 0)
                    {
                        a2uiMessages = retryMessages;
                        // O texto do primeiro passe já foi exibido; só usa o da
                        // reemissão quando o turno ficou sem nenhuma resposta.
                        if (string.IsNullOrWhiteSpace(fullContent) && !string.IsNullOrWhiteSpace(retryClean))
                            fullContent = retryClean;
                    }
                }
            }
            foreach (var a2uiMsg in a2uiMessages)
            {
                yield return new AiChatStreamChunk(Type: "a2ui", A2uiJson: a2uiMsg);
            }
            // Diagnóstico DEPOIS das mensagens: a UI já criou a bolha vazia, então
            // consegue removê-la e explicar POR QUE o card não apareceu (o evento
            // "chat:a2ui_incomplete" evita o aviso genérico do watchdog).
            if (AiChatA2uiExtractor.BuildIncompleteDiagnostic(a2uiMessages) is { } stillMissing)
            {
                yield return new AiChatStreamChunk(Type: "a2ui_incomplete", Content: stillMissing);
            }
        }

        // budgetExhausted: não tenta o fallback de tool calls textuais (XML/DSML)
        // — sem isso, o modelo driblaria o orçamento emitindo a tool como texto.
        var shouldTryXmlFallback = !budgetExhausted && (availableTools.Count == 0 || !hasToolCalls);
        if (shouldTryXmlFallback)
        {
            var (cleanedContent, updatedNextSeq) = await _toolOrchestrator.ParseAndExecuteXmlToolCallsAsync(
                fullContent, availableTools, scopeClientId, scopeSiteId, agentId,
                aiSettings, departmentId, llmMessages, toolMessagesToPersist,
                session.Id, nextSeq, traceId, ct);
            fullContent = cleanedContent;
            nextSeq = updatedNextSeq;
        }

        // ── Síntese forçada (agent loop resiliente) ──
        // Dispara quando o conteúdo final ficou vazio — inclusive quando o
        // orçamento de iterações esgotou com o LLM ainda querendo tools, ou
        // com toolIterations == 0 (ex.: só tool calls textuais sanitizados).
        // Faz até MaxSynthesisRetries chamadas SEM tools para garantir resposta.
        if (string.IsNullOrWhiteSpace(fullContent) && a2uiMessages.Count == 0)
        {
            // budgetExhausted: a nota de síntese já foi injetada no caminho
            // delegado — evita duplicá-la e dispara a síntese normal.
            if (hasToolCalls && !budgetExhausted && toolIterations >= maxIterations)
                llmMessages.Add(new LlmMessage("system", AiChatHelpers.SynthesisBudgetNote));
            else
                llmMessages.Add(new LlmMessage("user", AiChatHelpers.EmptyContentNote));

            for (var attempt = 1; attempt <= AiChatConstants.MaxSynthesisRetries && string.IsNullOrWhiteSpace(fullContent); attempt++)
            {
                contentBuilder.Clear(); // evita resíduo de tokens descartados
                var synthesisOptions = new LlmOptions(
                    maxTokens, AiChatHelpers.ClampTemperature(aiSettings),
                    string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                    string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                    string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                    false, null, aiSettings.Provider,
                    aiSettings.OpenRouterReferer, aiSettings.OpenRouterTitle, aiSettings.OpenRouterCategories,
                    SessionId: session!.Id.ToString("D"),
                    TimeoutMs: AiChatHelpers.ClampAiTimeoutMs(aiSettings),
                    // A7: amostragem configurável
                    TopP: AiChatHelpers.ClampTopP(aiSettings),
                    FrequencyPenalty: AiChatHelpers.ClampPenalty(aiSettings.FrequencyPenalty),
                    PresencePenalty: AiChatHelpers.ClampPenalty(aiSettings.PresencePenalty),
                    Seed: aiSettings.Seed,
                    ResponseFormat: aiSettings.ResponseFormat,
                    ReasoningEnabled: aiSettings.ReasoningEnabled, ReasoningEffort: aiSettings.ReasoningEffort);
                var synthFilter = new AiChatStreamFilter(aiSettings);
                await foreach (var token in _llmProvider.StreamAsync(systemPrompt!, llmMessages, synthesisOptions, ct))
                {
                    contentBuilder.Append(token);
                    var safeToken = synthFilter.Push(token);
                    if (!string.IsNullOrEmpty(safeToken))
                    {
                        yield return new AiChatStreamChunk(Type: "token", Content: safeToken);
                        anyTokenYielded = true;
                    }
                }
                var synthTail = synthFilter.Flush();
                if (!string.IsNullOrEmpty(synthTail))
                {
                    yield return new AiChatStreamChunk(Type: "token", Content: synthTail);
                    anyTokenYielded = true;
                }
                fullContent = AiChatGuardrails.ApplyOutputGuardrails(contentBuilder.ToString(), aiSettings);
                var (synthClean, synthA2ui, _) = AiChatOutputPipeline.Process(fullContent,
                    reason => _logger.LogWarning("[{TraceId}] A2UI (síntese) — {Reason}", traceId, reason));
                fullContent = synthClean;
                foreach (var a2uiJson in synthA2ui)
                    yield return new AiChatStreamChunk(Type: "a2ui", Content: a2uiJson);
                if (!string.IsNullOrWhiteSpace(fullContent)) break;
            }

            if (string.IsNullOrWhiteSpace(fullContent) && a2uiMessages.Count == 0)
            {
                fullContent = "Não foi possível gerar uma resposta. Tente reformular sua pergunta ou entre em contato com o suporte.";
                yield return new AiChatStreamChunk(Type: "token", Content: fullContent);
                anyTokenYielded = true;
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

        // B16: rede de segurança — nunca encerrar um turno 200 sem ter transmitido
        // o conteúdo que foi (ou será) persistido.
        if (!anyTokenYielded && !string.IsNullOrWhiteSpace(fullContent))
        {
            _logger.LogWarning("[{TraceId}] StreamAsync terminou sem nenhum token transmitido; emitindo conteúdo persistido ({Len} chars) como token final.", traceId, fullContent.Length);
            yield return new AiChatStreamChunk(Type: "token", Content: fullContent);
        }

        yield return new AiChatStreamChunk(Type: "done", SessionId: session.Id, LatencyMs: (int)stopwatch.ElapsedMilliseconds);
    }
}

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
    private readonly IMcpToolGovernance _governance;
    private readonly ILogger<AiChatService> _logger;
    private readonly AiChatSystemPromptBuilder _promptBuilder;
    private readonly AiChatToolOrchestrator _toolOrchestrator;
    private readonly AiChatQuickReply _quickReply;
    private readonly IAiTokenBudgetResolver _tokenBudgetResolver;
    private readonly IMemoryCache _memoryCache;
    // Catálogo de modelos: usado só para saber se o modelo configurado aceita
    // imagens (capacidade "vision") antes de enviar prints de tela.
    private readonly IAiModelCatalogService _modelCatalog;

    public AiChatStreamingOrchestrator(
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
        _sessionRepository = sessionRepository;
        _messageRepository = messageRepository;
        _agentRepository = agentRepository;
        _siteRepository = siteRepository;
        _llmProvider = llmProvider;
        _mcpToolExecutor = mcpToolExecutor;
        _governance = governance;
        _logger = logger;
        _promptBuilder = promptBuilder;
        _toolOrchestrator = toolOrchestrator;
        _quickReply = quickReply;
        _tokenBudgetResolver = tokenBudgetResolver;
        _memoryCache = memoryCache;
        _modelCatalog = modelCatalog;
    }

    /// <summary>
    /// Verifica se o modelo configurado aceita imagens. O catálogo (cache de
    /// 60 min) só é consultado quando existe imagem para enviar; qualquer falha
    /// mantém o comportamento anterior (envia) — nunca bloquear a captura por
    /// indisponibilidade do catálogo.
    /// </summary>
    private async Task<bool> ShouldSendScreenshotImagesAsync(
        AIIntegrationSettings settings, Guid clientId, Guid siteId, CancellationToken ct)
    {
        if (!settings.SendScreenshotImages) return false;
        var model = settings.ChatModel;
        if (string.IsNullOrWhiteSpace(model)) return true;
        try
        {
            var info = await _modelCatalog.GetModelAsync(
                clientId == Guid.Empty ? null : clientId,
                siteId == Guid.Empty ? null : siteId,
                model, ct);
            return AiChatHelpers.ResolveScreenshotImagesAllowed(true, info);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao resolver capacidade de visão do modelo {Model}; mantendo envio de imagens", model);
            return true;
        }
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

    /// <summary>
    /// Mapa tool→timeout (segundos) das tools do agente habilitadas, enviado ao
    /// agente no evento round_end para que ele aplique o timeout da política na
    /// execução local (o servidor não executa tools do agente).
    /// </summary>
    private static Dictionary<string, int> BuildToolTimeouts(
        IReadOnlyList<McpToolPolicy> policies, HashSet<string> agentToolNames)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in policies)
        {
            if (!agentToolNames.Contains(p.ToolName) || !p.IsEnabled || p.TimeoutSeconds <= 0)
                continue;
            map[p.ToolName] = p.TimeoutSeconds;
        }
        return map;
    }

    /// <summary>
    /// Consome o rate limit da tool do agente. Retorna a mensagem de erro JSON a
    /// devolver ao LLM quando o limite foi excedido, ou null quando permitido.
    /// Tool sem política cadastrada não tem limite (comportamento herdado).
    /// </summary>
    private async Task<string?> CheckAgentToolRateLimitAsync(
        string toolName, McpToolScope scope, IReadOnlyDictionary<string, McpToolPolicy> policies)
    {
        if (!policies.TryGetValue(toolName, out var policy))
            return null;

        if (await _governance.TryConsumeRateLimitAsync(toolName, scope, policy.MaxCallsPerMinute))
            return null;

        _logger.LogWarning("[MCP] Rate limit excedido para tool do agente {ToolName} (max {Max}/min, escopo {Level})",
            toolName, policy.MaxCallsPerMinute, scope.Level);

        return JsonSerializer.Serialize(new
        {
            error = $"Rate limit excedido para '{toolName}'. Máximo: {policy.MaxCallsPerMinute} chamadas por minuto. Aguarde antes de tentar novamente."
        });
    }

    /// <summary>
    /// B16: número de rejeições consecutivas de argumentos de uma tool do agent
    /// antes de encerrar o loop de tools e sintetizar uma resposta sem tools.
    /// </summary>
    private const int MaxConsecutiveAgentToolArgErrors = 3;

    /// <summary>
    /// Teto de tokens efetivo do chat: valor configurado no tenant limitado pela
    /// capacidade REAL do modelo e pelo teto do produto — o mesmo orçamento usado
    /// nos fluxos de IA de ticket. Sem isso, o chat aceitava a configuração do
    /// tenant mesmo quando o modelo não suporta aquele volume de saída.
    /// Falha do resolvedor não interrompe o chat: mantém o teto do tenant.
    /// </summary>
    private async Task<int> ResolveChatMaxTokensAsync(
        Guid siteId, AIIntegrationSettings aiSettings, CancellationToken ct)
    {
        var tenantMaxTokens = AiChatHelpers.ClampMaxTokens(aiSettings);

        try
        {
            var budget = await _tokenBudgetResolver.ResolveForSiteAsync(siteId, tenantMaxTokens, null, ct);
            return budget.MaxOutputTokens;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao resolver o orçamento de tokens do chat; usando o teto do tenant.");
            return tenantMaxTokens;
        }
    }

    /// <summary>
    /// Constrói a mensagem do usuário com partes multimodais quando há imagens
    /// anexadas (prints do chat). Sem imagens, comportamento anterior (string).
    /// </summary>
    private static LlmMessage BuildUserMessageWithImages(string message, List<string>? images)
    {
        var imageParts = AiChatHelpers.BuildImagePartsFromDataUrls(images);
        if (imageParts is null)
        {
            return new LlmMessage("user", message);
        }
        var parts = new List<LlmContentPart> { new("text", Text: message) };
        parts.AddRange(imageParts);
        return new LlmMessage("user", message, ContentParts: parts);
    }

    /// <summary>Trunca um texto para log sem depender de helpers externos.</summary>
    private static string Shorten(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max] + "...");

    public async IAsyncEnumerable<AiChatStreamChunk> StreamAsync(
        Guid agentId, string message, Guid? sessionId,
        Func<Guid, CancellationToken, Task<AIIntegrationSettings>> resolveAiSettings,
        Guid? departmentId = null,
        string? systemNote = null,
        List<string>? images = null,
        [EnumeratorCancellation] CancellationToken ct = default)
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

            if (!aiSettings.Enabled || !aiSettings.ChatAIEnabled)
                throw new InvalidOperationException("Chat IA está desabilitado para este escopo.");

            maxIterations = AiChatHelpers.ResolveMaxToolIterations(aiSettings);
            maxTokens = await ResolveChatMaxTokensAsync(scopeSiteId, aiSettings, ct);

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

            history = await _messageRepository.GetRecentBySessionAsync(session.Id,
                AiChatHelpers.ClampHistoryMessages(aiSettings), ct);
            nextSeq = history.Any() ? history.Max(m => m.SequenceNumber) + 1 : 1;

            (systemPrompt, _) = await _promptBuilder.BuildAsync(agent, session, message, aiSettings, departmentId, ct);

            llmMessages = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);
            if (!string.IsNullOrWhiteSpace(systemNote))
                llmMessages.Add(new LlmMessage("system", systemNote));
            // Guard de visão: só envia a imagem se o modelo aceitar (setting +
            // capacidade declarada no catálogo). Sem visão, o LLM recebe a
            // mensagem de texto e uma nota para avisar o usuário.
            var sendScreenshotImages = images is { Count: > 0 } &&
                await ShouldSendScreenshotImagesAsync(aiSettings, scopeClientId, scopeSiteId, ct);
            if (images is { Count: > 0 } && !sendScreenshotImages)
            {
                llmMessages.Add(new LlmMessage("system",
                    "[SISTEMA] O usuário anexou uma imagem, mas o modelo configurado não tem visão (ou o envio de prints está desabilitado). " +
                    "Avise o usuário que a imagem não pôde ser analisada e peça uma descrição em texto do que aparece na tela."));
            }
            llmMessages.Add(BuildUserMessageWithImages(message, sendScreenshotImages ? images : null));
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
        var availableTools = AiChatToolOrchestrator.MergeDistinctTools(kbTools, agentTools);
        if (agentTools is { Count: > 0 })
        {
            _logger.LogDebug("[{TraceId}] StreamAsync: {Count} agent tools mescladas ({Total} no total após dedupe)",
                traceId, agentTools.Count, availableTools.Count);
        }

        var agentToolCallNames = new HashSet<string>(agentTools?.Select(at => at.Name) ?? [], StringComparer.OrdinalIgnoreCase);
        // Governança: políticas efetivas (enable/disable, rate limit e timeout).
        var agentToolPolicies = await _toolOrchestrator.GetEffectivePoliciesAsync(scopeClientId, scopeSiteId, agentId, ct);
        var agentToolPolicyMap = agentToolPolicies.ToDictionary(p => p.ToolName, StringComparer.OrdinalIgnoreCase);
        var agentToolTimeouts = BuildToolTimeouts(agentToolPolicies, agentToolCallNames);
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
                TopP: aiSettings.TopP, FrequencyPenalty: aiSettings.FrequencyPenalty,
                PresencePenalty: aiSettings.PresencePenalty, Seed: aiSettings.Seed,
                ResponseFormat: aiSettings.ResponseFormat,
                ReasoningEnabled: aiSettings.ReasoningEnabled, ReasoningEffort: aiSettings.ReasoningEffort);

            hasToolCalls = false;
            hasAgentToolCallPending = false;
            providerError = null;

            if (roundTools.Count > 0)
            {
                await foreach (var evt in _llmProvider.StreamWithToolsAsync(systemPrompt, llmMessages, streamOptions, ct))
                {
                    if (evt.Type == "error")
                    {
                        providerError = evt.Content ?? "Erro no stream do provider";
                        break;
                    }
                    if (IsTextToken(evt))
                    {
                        contentBuilder.Append(evt.Content);
                        yield return new AiChatStreamChunk(Type: "token", Content: evt.Content);
                        anyTokenYielded = true;
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
                                var (isValid, errorJson) = AiChatToolOrchestrator.ValidateAgentToolArguments(toolCall.Name, toolCall.ArgumentsJson, agentToolSchemas?.GetValueOrDefault(toolCall.Name));
                                if (!isValid)
                                {
                                    var errCount = consecutiveToolErrors.GetValueOrDefault(toolCall.Name, 0) + 1;
                                    consecutiveToolErrors[toolCall.Name] = errCount;
                                    _logger.LogWarning("[{TraceId}] Argumentos rejeitados para tool do agent {ToolName} (tentativa {Count}): {Error} Args={Args}",
                                        traceId, toolCall.Name, errCount, errorJson, Shorten(toolCall.ArgumentsJson, 300));
                                    if (errCount >= MaxConsecutiveAgentToolArgErrors)
                                    {
                                        // B16: NÃO injetar fallback direto no contentBuilder (nunca
                                        // era transmitido ao cliente). Limpa o buffer e encerra o loop
                                        // de tools; a síntese forçada em streamDone tenta uma resposta
                                        // real e, se falhar, o fallback é emitido como token.
                                        _logger.LogWarning("[{TraceId}] Tool do agent {ToolName} rejeitada {Count}x por argumentos inválidos. Args={Args}. Encerrando loop de tools e sintetizando resposta.",
                                            traceId, toolCall.Name, errCount, Shorten(toolCall.ArgumentsJson, 300));
                                        contentBuilder.Clear();
                                        hasToolCalls = false;
                                        goto streamDone;
                                    }
                                    llmMessages.Add(new LlmMessage("tool", errorJson!, toolCall.Id, toolCall.Name));
                                    continue;
                                }

                                var rateError = await CheckAgentToolRateLimitAsync(
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
                                    msgs.Add(new AiChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq++, Role = "assistant", Content = contentBuilder.Length > 0 ? contentBuilder.ToString() : string.Empty, ToolCallsJson = JsonSerializer.Serialize(agentToolCallsPending.Select(tc => new { id = tc.Id, name = tc.Name, arguments = tc.ArgumentsJson })), CreatedAt = DateTime.UtcNow, TraceId = traceId });
                                await _messageRepository.CreateBatchAsync(msgs, ct);
                            }
                            catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir user message do round 1", traceId); }
                            yield return new AiChatStreamChunk(Type: "round_end", SessionId: session.Id,
                                ToolTimeouts: agentToolTimeouts.Count > 0 ? agentToolTimeouts : null);
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
                    anyTokenYielded = true;
                }
            }

            // B16-r2: erro estruturado do provider (objeto "error" no stream) —
            // encerra o turno com mensagem clara em vez de um done vazio.
            if (providerError is not null)
            {
                _logger.LogError("[{TraceId}] Provider stream error: {Error}", traceId, providerError);
                yield return new AiChatStreamChunk(Type: "error", Error: providerError);
                yield break;
            }

            // budgetExhausted: garante que o round sem tools aconteça mesmo que o
            // orçamento local (toolIterations) já tenha sido atingido; o break
            // natural ocorre depois, quando o round sem tools não pede tool.
            if (!hasToolCalls || (!budgetExhausted && toolIterations >= maxIterations - 1)) break;
            toolIterations++;
        }

    streamDone:
        stopwatch.Stop();
        var fullContent = contentBuilder.ToString();

        // ── A2UI + sanitização de vazamentos (ordem única, ver AiChatOutputPipeline) ──
        // A extração A2UI DEVE rodar antes da sanitização: o sanitizador apaga
        // blocos ```a2ui como fallback e, se rodasse primeiro, o agent nunca
        // receberia o chunk "a2ui" (a interface sumia do chat).
        var (cleanContent, a2uiMessages, contentWasSanitized) = AiChatOutputPipeline.Process(fullContent);
        if (contentWasSanitized)
        {
            _logger.LogInformation("[{TraceId}] Vazamentos de tool call removidos do output ({OrigLen} -> {CleanLen} chars)",
                traceId, fullContent.Length, cleanContent.Length);
        }
        fullContent = cleanContent;
        foreach (var a2uiMsg in a2uiMessages)
        {
            yield return new AiChatStreamChunk(Type: "a2ui", A2uiJson: a2uiMsg);
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
        if (string.IsNullOrWhiteSpace(fullContent))
        {
            // budgetExhausted: a nota de síntese já foi injetada no caminho
            // delegado — evita duplicá-la e dispara a síntese normal.
            if (hasToolCalls && !budgetExhausted && toolIterations >= maxIterations - 1)
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
                TopP: aiSettings.TopP, FrequencyPenalty: aiSettings.FrequencyPenalty,
                PresencePenalty: aiSettings.PresencePenalty, Seed: aiSettings.Seed,
                ResponseFormat: aiSettings.ResponseFormat,
                ReasoningEnabled: aiSettings.ReasoningEnabled, ReasoningEffort: aiSettings.ReasoningEffort);
                await foreach (var token in _llmProvider.StreamAsync(systemPrompt!, llmMessages, synthesisOptions, ct))
                {
                    contentBuilder.Append(token);
                    yield return new AiChatStreamChunk(Type: "token", Content: token);
                    anyTokenYielded = true;
                }
                fullContent = contentBuilder.ToString();
                if (!string.IsNullOrWhiteSpace(fullContent)) break;
            }

            if (string.IsNullOrWhiteSpace(fullContent))
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

        var maxTokens = await ResolveChatMaxTokensAsync(session.SiteId, aiSettings, ct);

        var history = await _messageRepository.GetRecentBySessionAsync(session.Id, AiChatHelpers.ClampHistoryMessages(aiSettings), ct);
        var nextSeq = history.Any() ? history.Max(m => m.SequenceNumber) + 1 : 1;

        var llmMessages = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(history);

        if (!string.IsNullOrWhiteSpace(systemNote))
            llmMessages.Add(new LlmMessage("system", systemNote));

        // B6: guarda de input também no multi-round (antes só StreamAsync validava).
        if (!string.IsNullOrWhiteSpace(message))
            AiChatGuardrails.ValidateUserInput(message, AiChatConstants.MaxMessageSizeBytes);

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

        // Guard de visão: detectado uma única vez por request (o conteúdo dos
        // tool results já está em memória) e aplicado a todos os prints do round.
        var hasScreenshotResult = toolResults is { Count: > 0 } &&
            toolResults.Any(tr => tr.Result.Contains("\"image_base64\"", StringComparison.Ordinal));
        var sendScreenshotImages = hasScreenshotResult &&
            await ShouldSendScreenshotImagesAsync(aiSettings, session.ClientId, session.SiteId, ct);
        if (hasScreenshotResult && !sendScreenshotImages)
        {
            llmMessages.Add(new LlmMessage("system",
                "[SISTEMA] Uma captura de tela foi executada, mas o modelo atual não tem visão (ou o envio de prints está desabilitado). " +
                "Explique ao usuário que a imagem não pôde ser analisada e ofereça alternativas (ex.: descrever o erro em texto)."));
        }

        if (toolResults is { Count: > 0 })
        {
            // B1: valida cada toolResult contra as tool calls pendentes emitidas
            // no round anterior (último assistant com ToolCallsJson). Sem isso,
            // um agent comprometido podia injetar resultado falso de qualquer
            // tool (ex.: sucesso de install_package).
            List<LlmAssistantToolCall>? pendingCalls = null;
            var lastAssistantWithCalls = history.LastOrDefault(m =>
                m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.ToolCallsJson));
            if (lastAssistantWithCalls != null)
            {
                try { pendingCalls = AiChatToolOrchestrator.ParseToolCallsFromJson(lastAssistantWithCalls.ToolCallsJson); }
                catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao parsear ToolCallsJson para validação B1", traceId); }
            }
            var pendingIds = new HashSet<string>(pendingCalls?.Select(c => c.Id) ?? [], StringComparer.OrdinalIgnoreCase);
            var pendingNames = new HashSet<string>(pendingCalls?.Select(c => c.Name) ?? [], StringComparer.OrdinalIgnoreCase);

            var toolMsgs = new List<AiChatMessage>();
            // Imagens de captura de tela são adicionadas DEPOIS de todos os tool
            // messages: intercalar mensagens user entre tool messages pode ser
            // rejeitado por provedores OpenAI-compatible.
            var toolImageMessages = new List<LlmMessage>();
            foreach (var tr in toolResults)
            {
                // "a2ui_action" é a sentinela de interação com surfaces (não é
                // tool call real) — permitida sempre.
                var isA2uiAction = string.Equals(tr.Name, "a2ui_action", StringComparison.OrdinalIgnoreCase);
                if (pendingCalls != null && !isA2uiAction
                    && (!pendingIds.Contains(tr.CallId) || !pendingNames.Contains(tr.Name)))
                {
                    _logger.LogWarning("[{TraceId}] ToolResult rejeitado (não corresponde a tool call pendente): CallId={CallId}, Name={Name}, SessionId={SessionId}",
                        traceId, tr.CallId, tr.Name, session.Id);
                    continue;
                }

                var wrapped = AiChatToolOrchestrator.WrapAgentToolError(tr.Result, tr.Name);
                // Captura de tela: o tool result pode conter a imagem (data URL).
                // Injeta uma mensagem user multimodal APÓS o tool message — é o
                // formato aceito por OpenAI/OpenRouter para visão em tool calls.
                var imageParts = sendScreenshotImages
                    ? AiChatHelpers.BuildImagePartsFromToolResult(wrapped, tr.Name)
                    : null;
                // CRÍTICO (turno real de 2026-10-01 12:01Z): quando a imagem vai
                // na mensagem multimodal, o CONTEÚDO da tool message NÃO pode
                // repetir o base64 — isso dobrava o payload enviado ao provedor
                // (~840 KB em 2 capturas) e provocava
                // "Provider stream error: Request could not be processed".
                // O texto compactado preserva metadados (mime/note/window).
                var toolMessageContent = imageParts is not null
                    ? AiChatHelpers.CompactToolResultForPersistence(wrapped)
                    : wrapped;
                // B2: usa o CallId ORIGINAL do tool call — o assistant do round
                // anterior foi persistido com tc.Id; o prefixo "agent_" quebrava
                // o pareamento tool_call/tool_message em OpenAI/DeepSeek.
                llmMessages.Add(new LlmMessage("tool", toolMessageContent, tr.CallId, tr.Name));
                if (imageParts is not null)
                {
                    toolImageMessages.Add(new LlmMessage(
                        "user",
                        $"Imagem capturada pela ferramenta {tr.Name} para análise visual.",
                        ContentParts: imageParts));
                }
                // Persistência usa o mesmo conteúdo compactado (sem base64),
                // evitando MBs por captura em ai_chat_messages.
                toolMsgs.Add(new AiChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq++, Role = "tool", Content = toolMessageContent, ToolCallId = tr.CallId, ToolName = tr.Name, CreatedAt = DateTime.UtcNow, TraceId = traceId });
            }
            llmMessages.AddRange(toolImageMessages);
            try { await _messageRepository.CreateBatchAsync(toolMsgs, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir tool results", traceId); }
        }

        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null) { yield return new AiChatStreamChunk(Type: "error", Error: "Agent não encontrado."); yield break; }

        // B13: usa a última mensagem do usuário do histórico (o 1º toolResult
        // pode ser ruído e degradava o contexto do prompt).
        var lastUserMessage = history.LastOrDefault(m => m.Role == "user")?.Content;
        // O seed do prompt (RAG/embedding) nunca pode ser um tool result com
        // base64 de imagem: Shorten limita o custo e evita embutir MBs.
        var promptSeed = message ?? lastUserMessage ?? Shorten(toolResults?.FirstOrDefault()?.Result, 2000) ?? "";
        var (systemPrompt, _) = await _promptBuilder.BuildAsync(agent, session,
            promptSeed, aiSettings, departmentId, ct);

        var kbTools = aiSettings.KnowledgeBaseEnabled
            ? await _mcpToolExecutor.GetAvailableToolsAsync(session.ClientId, session.SiteId, agentId, ct)
            : new List<LlmTool>();
        var agentTools = await _toolOrchestrator.GetAgentToolsForScopeAsync(agentId, session.ClientId, session.SiteId, ct);
        var availableTools = AiChatToolOrchestrator.MergeDistinctTools(kbTools, agentTools);

        var maxIterations = AiChatHelpers.ResolveMaxToolIterations(aiSettings);

        // Round do TURNO (não do request): ToolResults presentes = continuação de
        // uma tool chain já iniciada (round anterior + 1); mensagem nova do usuário
        // sem ToolResults = novo turno (= 1). A chave na cache sobrevive entre os
        // requests do mesmo turno — antes o contador local zerava e o heartbeat
        // mostrava sempre "round 1", e o orçamento configurado era ignorado no
        // caminho delegado ao agent.
        var turnRound = AiChatTurnRound.Resolve(_memoryCache, session.Id, toolResults is { Count: > 0 });
        // Round de síntese forçada sem tools após o orçamento esgotar.
        bool budgetExhausted = false;

        // ── Watchdog de round pendente ──
        // Se EXISTE registro de round delegado ao agent e este request NÃO
        // traz ToolResults (ex.: usuário mandou nova mensagem), o round nunca
        // foi concluído — injeta nota de expiração para o LLM concluir com
        // resposta em vez de deixar a conversa "aberta".
        // IMPORTANTE: quando ToolResults chegam, o round foi concluído com
        // sucesso — apenas remove o registro SEM injetar nota (TryGetValue
        // retorna true enquanto a chave não expirou, então a presença da
        // chave sozinha NÃO significa expiração).
        var pendingKey = $"pending_round:{session.Id}";
        if (_memoryCache.TryGetValue(pendingKey, out _))
        {
            _memoryCache.Remove(pendingKey);
            if (toolResults is not { Count: > 0 })
            {
                llmMessages.Add(new LlmMessage("system", AiChatHelpers.AgentRoundExpiredNote));
                _logger.LogWarning("[{TraceId}] Round pendente sem ToolResults para SessionId={SessionId} — nota de expiração injetada", traceId, session.Id);
            }
        }

        var contentBuilder = new StringBuilder();
        var toolIterations = 0;
        int? totalTokens = null;
        var executedKbQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consecutiveEmptyKbSearches = 0;
        bool hasToolCalls = false;
        // B16: rastreia se algum token visível já foi transmitido ao cliente.
        bool anyTokenYielded = false;
        // B16-r2: erro estruturado do provider (objeto "error" no stream do LLM).
        string? providerError = null;
        var agentToolCallNames = new HashSet<string>(agentTools?.Select(at => at.Name) ?? [], StringComparer.OrdinalIgnoreCase);
        // Governança: políticas efetivas (enable/disable, rate limit e timeout).
        var agentToolPolicies = await _toolOrchestrator.GetEffectivePoliciesAsync(session.ClientId, session.SiteId, agentId, ct);
        var agentToolPolicyMap = agentToolPolicies.ToDictionary(p => p.ToolName, StringComparer.OrdinalIgnoreCase);
        var agentToolTimeouts = BuildToolTimeouts(agentToolPolicies, agentToolCallNames);
        // B17: schema registrado por tool (validação de argumentos agnóstica de modelo).
        // GroupBy evita exceção de chave duplicada se o agent registrar nomes repetidos.
        var agentToolSchemas = agentTools?.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Schema, StringComparer.OrdinalIgnoreCase);
        var consecutiveToolErrors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool kbExhausted = false;

        while (true)
        {
            // Heartbeat de progresso do agent loop (só com tools disponíveis).
            // Omitido no round de síntese (budgetExhausted): não há mais loop.
            if (availableTools.Count > 0 && !budgetExhausted)
                yield return new AiChatStreamChunk(Type: "loop_progress", LoopRound: turnRound, LoopMaxRounds: maxIterations);

            // budgetExhausted: round de síntese roda sem tools para impedir que o
            // modelo peça outra tool e drible o orçamento configurado.
            var roundTools = budgetExhausted
                ? new List<LlmTool>()
                : kbExhausted
                    ? availableTools.Where(t => t.Name != "knowledge_search").ToList()
                    : availableTools;

            var streamOptions = new LlmOptions(
                AiChatHelpers.ClampMaxTokens(aiSettings), AiChatHelpers.ClampTemperature(aiSettings),
                string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                roundTools.Count > 0, roundTools, aiSettings.Provider,
                aiSettings.OpenRouterReferer, aiSettings.OpenRouterTitle, aiSettings.OpenRouterCategories,
                SessionId: session.Id.ToString("D"),
                TimeoutMs: AiChatHelpers.ClampAiTimeoutMs(aiSettings),
                // A7: amostragem configurável
                TopP: aiSettings.TopP, FrequencyPenalty: aiSettings.FrequencyPenalty,
                PresencePenalty: aiSettings.PresencePenalty, Seed: aiSettings.Seed,
                ResponseFormat: aiSettings.ResponseFormat,
                ReasoningEnabled: aiSettings.ReasoningEnabled, ReasoningEffort: aiSettings.ReasoningEffort);

            hasToolCalls = false;
            bool hasAgentToolCall = false;
            // Calls do agent suprimidas por orçamento esgotado: fechadas com
            // tool message sintético para o round de síntese não receber 400.
            var agentToolCallsPending = new List<LlmAssistantToolCall>();
            providerError = null;

            if (roundTools.Count > 0)
            {
                await foreach (var evt in _llmProvider.StreamWithToolsAsync(systemPrompt, llmMessages, streamOptions, ct))
                {
                    if (evt.Type == "error")
                    {
                        providerError = evt.Content ?? "Erro no stream do provider";
                        break;
                    }
                    if (IsTextToken(evt))
                    {
                        contentBuilder.Append(evt.Content);
                        yield return new AiChatStreamChunk(Type: "token", Content: evt.Content);
                        anyTokenYielded = true;
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
                                var (isValid, errorJson) = AiChatToolOrchestrator.ValidateAgentToolArguments(tc.Name, tc.ArgumentsJson, agentToolSchemas?.GetValueOrDefault(tc.Name));
                                if (!isValid)
                                {
                                    var errCount = consecutiveToolErrors.GetValueOrDefault(tc.Name, 0) + 1;
                                    consecutiveToolErrors[tc.Name] = errCount;
                                    _logger.LogWarning("[{TraceId}] Argumentos rejeitados para tool do agent {ToolName} (tentativa {Count}): {Error} Args={Args}",
                                        traceId, tc.Name, errCount, errorJson, Shorten(tc.ArgumentsJson, 300));
                                    if (errCount >= MaxConsecutiveAgentToolArgErrors)
                                    {
                                        // B16: encerra o loop de tools sem injetar fallback no
                                        // buffer (que nunca era transmitido). A síntese forçada
                                        // abaixo tenta uma resposta real.
                                        _logger.LogWarning("[{TraceId}] Tool do agent {ToolName} rejeitada {Count}x por argumentos inválidos. Args={Args}. Encerrando loop de tools e sintetizando resposta.",
                                            traceId, tc.Name, errCount, Shorten(tc.ArgumentsJson, 300));
                                        contentBuilder.Clear();
                                        hasToolCalls = false;
                                        goto streamMultiRoundDone;
                                    }
                                    llmMessages.Add(new LlmMessage("tool", errorJson!, tc.Id, tc.Name));
                                    continue;
                                }
                                var rateError = await CheckAgentToolRateLimitAsync(
                                    tc.Name, new McpToolScope(session.ClientId, session.SiteId, agentId), agentToolPolicyMap);
                                if (rateError is not null)
                                {
                                    llmMessages.Add(new LlmMessage("tool", rateError, tc.Id, tc.Name));
                                    continue;
                                }

                                hasAgentToolCall = true;
                                agentToolCallsPending.Add(new LlmAssistantToolCall(tc.Id, tc.Name, tc.ArgumentsJson));
                                // Orçamento esgotado: não emite o tool_call; o bloco
                                // pós-foreach injeta a nota de síntese e reinicia o
                                // round sem tools (ver budgetExhausted).
                                if (!AiChatTurnRound.IsBudgetExhausted(turnRound, maxIterations))
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
                                // B14: sucesso reseta o contador de erros da tool.
                                consecutiveToolErrors.Remove(tc.Name);
                                if (tc.Name == "knowledge_search" && toolResult.Contains("\"found\":false")) consecutiveEmptyKbSearches++;

                                if (consecutiveEmptyKbSearches >= 2 && !kbExhausted)
                                {
                                    kbExhausted = true;
                                    llmMessages.Add(new LlmMessage("system", AiChatHelpers.KbExhaustedNote));
                                }
                            }
                        }

                        if (hasAgentToolCall)
                        {
                            // Orçamento do turno esgotado: este request iria delegar
                            // outra tool ao agent, mas o máximo configurado já foi
                            // alcançado. Não emite round_end nem persiste round
                            // pendente (antes o yield break ignorava totalmente o
                            // MaxToolCallIterations). Injeta a nota de síntese e
                            // reinicia o round sem tools.
                            if (AiChatTurnRound.IsBudgetExhausted(turnRound, maxIterations))
                            {
                                budgetExhausted = true;
                                hasAgentToolCall = false;
                                // Fecha as tool_calls já presentes em llmMessages (a
                                // delegação ao agent foi abortada) para o round de
                                // síntese sem tools não receber 400 do provedor.
                                foreach (var pendingCall in agentToolCallsPending)
                                    llmMessages.Add(new LlmMessage("tool", AiChatHelpers.AgentToolBudgetExhaustedResult, pendingCall.Id, pendingCall.Name));
                                llmMessages.Add(new LlmMessage("system", AiChatHelpers.SynthesisBudgetNote));
                                _logger.LogInformation(
                                    "[{TraceId}] Orçamento de rounds do turno esgotado ({Round}/{Max}); sintetizando resposta sem tools em vez de delegar ao agent",
                                    traceId, turnRound, maxIterations);
                                // Sai do await foreach: o while reentra com roundTools
                                // vazio (sem chamada ao LLM aqui); a resposta final sai
                                // da síntese forçada pós-loop.
                                break;
                            }
                            // Registra round pendente para o watchdog: se o agent não
                            // devolver ToolResults dentro do TTL, o próximo multi-round
                            // desta sessão injeta nota de expiração.
                            _memoryCache.Set(pendingKey, DateTime.UtcNow, AiChatConstants.PendingRoundTtl);
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
                            yield return new AiChatStreamChunk(Type: "round_end", SessionId: session.Id,
                                ToolTimeouts: agentToolTimeouts.Count > 0 ? agentToolTimeouts : null);
                            yield break;
                        }
                    }
                    else if (evt.Type == "done") { totalTokens = evt.TokensUsed; }
                }
            }

            // budgetExhausted: garante que o while rode o round sem tools mesmo
            // com o orçamento local atingido. No multi-round esse round NÃO faz
            // chamada ao LLM (não existe else para roundTools vazio); ele só
            // deixa o loop encerrar e a síntese forçada pós-loop responde.
            if (!hasToolCalls || (!budgetExhausted && toolIterations >= maxIterations - 1)) break;
            toolIterations++;
        }

        // B16-r2: erro estruturado do provider — encerra o turno com error
        // (o controller repassa ao agent) em vez de seguir para a síntese.
        if (providerError is not null)
        {
            _logger.LogError("[{TraceId}] Provider stream error (multi-round): {Error}", traceId, providerError);
            yield return new AiChatStreamChunk(Type: "error", Error: providerError);
            yield break;
        }

    streamMultiRoundDone:
        stopwatch.Stop();
        var fullContent = contentBuilder.ToString();

        // ── A2UI + sanitização de vazamentos (mesma ordem única do StreamAsync) ──
        // Roda ANTES da síntese forçada: se o conteúdo era só vazamento (DSML,
        // invokes textuais), a sanitização o esvazia e a síntese dispara com a
        // nota correta. A extração A2UI vem PRIMEIRO para o sanitizador não
        // apagar o bloco ```a2ui antes de ele virar chunk "a2ui".
        var (cleanMultiContent, a2uiMultiMessages, multiWasSanitized) = AiChatOutputPipeline.Process(fullContent);
        if (multiWasSanitized)
        {
            _logger.LogInformation("[{TraceId}] Vazamentos de tool call removidos do output multi-round ({OrigLen} -> {CleanLen} chars)",
                traceId, fullContent.Length, cleanMultiContent.Length);
        }
        fullContent = cleanMultiContent;
        foreach (var a2uiMsg in a2uiMultiMessages)
        {
            yield return new AiChatStreamChunk(Type: "a2ui", A2uiJson: a2uiMsg);
        }

        // ── Síntese forçada: conteúdo vazio (ou orçamento estourado) → chamadas
        // sem tools até obter resposta ──
        // budgetExhausted entra na condição de propósito: aqui um round sem
        // tools NÃO chama o LLM dentro do while, então a resposta final sai
        // daqui. Se o modelo tivesse deixado texto residual depois do
        // tool_calls suprimido, depender só de "conteúdo vazio" encerraria o
        // turno sem resposta.
        if (string.IsNullOrWhiteSpace(fullContent) || budgetExhausted)
        {
            // budgetExhausted: a nota já foi injetada no caminho delegado.
            if (hasToolCalls && !budgetExhausted && toolIterations >= maxIterations - 1)
                llmMessages.Add(new LlmMessage("system", AiChatHelpers.SynthesisBudgetNote));
            else
                llmMessages.Add(new LlmMessage("user", AiChatHelpers.EmptyContentNote));

            for (var attempt = 1; attempt <= AiChatConstants.MaxSynthesisRetries && string.IsNullOrWhiteSpace(fullContent); attempt++)
            {
                contentBuilder.Clear();
                var synthesisOptions = new LlmOptions(
                    maxTokens, AiChatHelpers.ClampTemperature(aiSettings),
                    string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                    string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                    string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                    false, null, aiSettings.Provider,
                    aiSettings.OpenRouterReferer, aiSettings.OpenRouterTitle, aiSettings.OpenRouterCategories,
                    SessionId: session.Id.ToString("D"),
                    TimeoutMs: AiChatHelpers.ClampAiTimeoutMs(aiSettings),
                // A7: amostragem configurável
                TopP: aiSettings.TopP, FrequencyPenalty: aiSettings.FrequencyPenalty,
                PresencePenalty: aiSettings.PresencePenalty, Seed: aiSettings.Seed,
                ResponseFormat: aiSettings.ResponseFormat,
                ReasoningEnabled: aiSettings.ReasoningEnabled, ReasoningEffort: aiSettings.ReasoningEffort);
                await foreach (var token in _llmProvider.StreamAsync(systemPrompt, llmMessages, synthesisOptions, ct))
                {
                    contentBuilder.Append(token);
                    yield return new AiChatStreamChunk(Type: "token", Content: token);
                    anyTokenYielded = true;
                }
                fullContent = contentBuilder.ToString();
            }
        }

        if (string.IsNullOrWhiteSpace(fullContent))
        {
            fullContent = "Não foi possível gerar uma resposta. Tente reformular sua pergunta.";
            // B16: o fallback precisa ser transmitido (antes só era persistido).
            yield return new AiChatStreamChunk(Type: "token", Content: fullContent);
            anyTokenYielded = true;
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

        // B16: rede de segurança — nunca encerrar um turno 200 sem conteúdo visível.
        if (!anyTokenYielded && !string.IsNullOrWhiteSpace(fullContent))
        {
            _logger.LogWarning("[{TraceId}] Multi-round terminou sem nenhum token transmitido; emitindo conteúdo persistido ({Len} chars) como token final.", traceId, fullContent.Length);
            yield return new AiChatStreamChunk(Type: "token", Content: fullContent);
        }

        yield return new AiChatStreamChunk(Type: "done", SessionId: session.Id,
            TokensUsed: totalTokens, LatencyMs: (int)stopwatch.ElapsedMilliseconds);
    }
}

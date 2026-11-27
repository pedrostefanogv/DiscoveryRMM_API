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

public class AiChatMultiRoundStreamer
{
    private readonly IAiChatSessionRepository _sessionRepository;
    private readonly IAiChatMessageRepository _messageRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly ILlmProvider _llmProvider;
    private readonly IMcpToolExecutor _mcpToolExecutor;
    private readonly ILogger<AiChatService> _logger;
    private readonly AiChatSystemPromptBuilder _promptBuilder;
    private readonly AiChatToolOrchestrator _toolOrchestrator;
    private readonly IMemoryCache _memoryCache;
    private readonly AiChatCapabilityResolver _capabilities;
    private readonly AiChatToolRateLimiter _rateLimiter;
    private readonly IAiCostControlService? _costControl;
    private readonly IMcpToolAuditService? _auditService;

    public AiChatMultiRoundStreamer(
        IAiChatSessionRepository sessionRepository,
        IAiChatMessageRepository messageRepository,
        IAgentRepository agentRepository,
        ILlmProvider llmProvider,
        IMcpToolExecutor mcpToolExecutor,
        ILogger<AiChatService> logger,
        AiChatSystemPromptBuilder promptBuilder,
        AiChatToolOrchestrator toolOrchestrator,
        IMemoryCache memoryCache,
        AiChatCapabilityResolver capabilities,
        AiChatToolRateLimiter rateLimiter,
        IAiCostControlService? costControl = null,
        IMcpToolAuditService? auditService = null)
    {
        _sessionRepository = sessionRepository;
        _messageRepository = messageRepository;
        _agentRepository = agentRepository;
        _llmProvider = llmProvider;
        _mcpToolExecutor = mcpToolExecutor;
        _logger = logger;
        _promptBuilder = promptBuilder;
        _toolOrchestrator = toolOrchestrator;
        _memoryCache = memoryCache;
        _capabilities = capabilities;
        _rateLimiter = rateLimiter;
        _costControl = costControl;
        _auditService = auditService;
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

        var costAcquired = false;
        if (_costControl is not null)
        {
            costAcquired = await _costControl.TryAcquireAsync(session.ClientId, session.SiteId, aiSettings, ct);
            if (!costAcquired)
            {
                yield return new AiChatStreamChunk(Type: "error", Error: "Limite de uso ou orçamento de IA excedido para este escopo.");
                yield break;
            }
        }

        var maxTokens = await _capabilities.ResolveChatMaxTokensAsync(session.SiteId, aiSettings, ct);

        var historyLimit = AiChatHelpers.ClampHistoryMessages(aiSettings);
        var history = await _messageRepository.GetRecentBySessionAsync(session.Id, historyLimit + 12, ct);
        var nextSeq = history.Any() ? history.Max(m => m.SequenceNumber) + 1 : 1;

        var llmMessages = AiChatHelpers.BuildCompactedLlmMessages(history, historyLimit);

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
            await _capabilities.ShouldSendScreenshotImagesAsync(aiSettings, session.ClientId, session.SiteId, ct);
        if (hasScreenshotResult && !sendScreenshotImages)
        {
            llmMessages.Add(new LlmMessage("system",
                "[SISTEMA] Uma captura de tela foi executada, mas o modelo atual não tem visão (ou o envio de prints está desabilitado). " +
                "Explique ao usuário que a imagem não pôde ser analisada e ofereça alternativas (ex.: descrever o erro em texto)."));
        }

        // Capacidade A2UI DESLIGADA + clique numa interface JÁ renderizada (de
        // antes da mudança): o tool result sintético a2ui_action instrui o modelo
        // a reemitir a surface, o que contraria a governança. A contra-ordem
        // evita que o modelo gaste tokens (e prometa atualização que o servidor
        // descarta).
        if (toolResults is { Count: > 0 }
            && toolResults.Any(tr => string.Equals(tr.Name, AiChatTurnRound.A2uiActionToolName, StringComparison.OrdinalIgnoreCase))
            && !await _capabilities.IsA2uiEnabledAsync(session.ClientId, session.SiteId, agentId, ct))
        {
            llmMessages.Add(new LlmMessage("system",
                "[SISTEMA] A interface A2UI está DESABILITADA nesta configuração (MCP tools). NÃO emita blocos a2ui nem prometa atualizar a interface: " +
                "responda em texto/markdown e, se o clique pedia uma ação real (ex.: abrir pasta/app), execute a ferramenta correspondente normalmente."));
        }

        // Round de síntese forçada sem tools após o orçamento esgotar.
        bool budgetExhausted = false;

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
            var pendingPairs = new HashSet<(string Id, string Name)>(
                pendingCalls?.Select(c => (c.Id ?? string.Empty, c.Name ?? string.Empty)) ?? [],
                new CaseInsensitiveTupleComparer());

            // B8-r2 (500 "Duplicate function_call_output", caso
            // call_856c0949dcd0b375_2 / sessão 01a1cd68: seq 16 e 17 com o MESMO
            // tool_call_id): a validação B1 acima é de PERTENCIMENTO — ela diz
            // que o CallId existe entre as tool calls pendentes, NÃO que ele
            // ainda está SEM RESPOSTA. O agent reenvia por retry/duplicata um
            // tool result já respondido e o lote era persistido inteiro; no
            // round seguinte BuildLlmMessagesFromHistory (OrderBy
            // SequenceNumber) convertia as duas linhas em DOIS
            // function_call_output para o mesmo call_id -> provedor 400 ->
            // HTTP 500 no cliente.
            // answeredCallIds: call_ids JÁ respondidos no histórico carregado
            // (role='tool'). a2ui_action fica FORA: é sentinela de clique em
            // surface (não é tool call real) e pode legitimamente repetir.
            var answeredCallIds = new HashSet<string>(
                history.Where(m => m.Role == "tool"
                        && !string.IsNullOrWhiteSpace(m.ToolCallId)
                        && !string.Equals(m.ToolName, AiChatTurnRound.A2uiActionToolName, StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.ToolCallId!),
                StringComparer.OrdinalIgnoreCase);
            // CallIds já aceitos NESTE lote: cobre o mesmo CallId repetido
            // dentro do próprio toolResults.
            var acceptedCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var toolMsgs = new List<AiChatMessage>();
            // Imagens de captura de tela são adicionadas DEPOIS de todos os tool
            // messages: intercalar mensagens user entre tool messages pode ser
            // rejeitado por provedores OpenAI-compatible.
            var toolImageMessages = new List<LlmMessage>();
            // P2.11: alguma tool deste lote falhou por indisponibilidade LOCAL
            // não-retentável (canal de gravação do agente fora). Ver a marcação
            // dentro do foreach.
            var localWriteUnavailable = false;
            foreach (var tr in toolResults)
            {
                // "a2ui_action" é a sentinela de interação com surfaces (não é
                // tool call real) — permitida sempre.
                var isA2uiAction = string.Equals(tr.Name, "a2ui_action", StringComparison.OrdinalIgnoreCase);
                if (!isA2uiAction && (pendingCalls == null || !pendingPairs.Contains((tr.CallId ?? string.Empty, tr.Name ?? string.Empty))))
                {
                    _logger.LogWarning("[{TraceId}] ToolResult rejeitado (não corresponde a tool call pendente): CallId={CallId}, Name={Name}, SessionId={SessionId}",
                        traceId, tr.CallId, tr.Name, session.Id);
                    continue;
                }

                // B8-r2: dedupe por CallId. Um call_id que o provedor já
                // recebeu como function_call_output não pode ser respondido
                // duas vezes — o provedor rejeita o round INTEIRO com 400
                // ("Each function_call must have exactly one matching
                // function_call_output") e o cliente vê HTTP 500.
                // (a) já respondido no histórico persistido da sessão;
                // (b) repetido dentro do PRÓPRIO lote toolResults.
                // A sentinela a2ui_action é isenta nas duas checagens.
                if (!isA2uiAction)
                {
                    if (answeredCallIds.Contains(tr.CallId ?? string.Empty))
                    {
                        _logger.LogWarning("[{TraceId}] ToolResult descartado (call_id já respondido no histórico — evitar function_call_output duplicado): CallId={CallId}, Name={Name}, SessionId={SessionId}",
                            traceId, tr.CallId, tr.Name, session.Id);
                        continue;
                    }
                    if (!acceptedCallIds.Add(tr.CallId ?? string.Empty))
                    {
                        _logger.LogWarning("[{TraceId}] ToolResult descartado (CallId duplicado dentro do próprio lote): CallId={CallId}, Name={Name}, SessionId={SessionId}",
                            traceId, tr.CallId, tr.Name, session.Id);
                        continue;
                    }
                }

                // P2.11 (PLANO_CORRECAO_CANAL_ADMIN_IPC.md §4): falha LOCAL
                // não-retentável do canal de gravação do agente. Diferente das
                // demais falhas, esta NÃO pode virar novo round de ferramenta:
                // o serviço do agente está em modo somente-leitura e qualquer
                // outra mutação repetiria o mesmo erro (3 × create_ticket no
                // incidente de 2026-10-17, ~38s). A flag sobrevive ao foreach
                // para abortar o loop DEPOIS de fechar as tool messages (um
                // break no meio deixaria tool_call sem tool message → HTTP 400).
                if (AiChatToolOrchestrator.IsNonRetryableLocalAgentWrite(tr.Result))
                {
                    localWriteUnavailable = true;
                    _logger.LogWarning(
                        "[{TraceId}] Falha LOCAL não-retentável do canal de gravação do agente na tool {ToolName} — abortando o loop de ferramentas deste turno",
                        traceId, tr.Name);
                }

                var wrapped = AiChatToolOrchestrator.WrapAgentToolError(tr.Result, tr.Name ?? string.Empty);
                if (_auditService is not null)
                {
                    var agentStatus = tr.Result.Contains("\"approved\":false", StringComparison.OrdinalIgnoreCase)
                        || tr.Result.Contains("\"approved\": false", StringComparison.OrdinalIgnoreCase)
                        ? "denied"
                        : tr.Result.Contains("\"error\"", StringComparison.OrdinalIgnoreCase)
                            ? "error"
                            : "success";
                    await _auditService.RecordAsync(new McpToolAuditLog
                    {
                        ToolName = tr.Name ?? string.Empty,
                        Origin = "agent",
                        ClientId = session.ClientId,
                        SiteId = session.SiteId,
                        AgentId = agentId,
                        SessionId = session.Id,
                        Status = agentStatus,
                        DurationMs = 0,
                        ErrorMessage = agentStatus == "success" ? null : AiChatCapabilityResolver.Shorten(tr.Result, 300),
                        CreatedAt = DateTime.UtcNow
                    }, ct);
                }
                // Captura de tela: o tool result pode conter a imagem (data URL).
                // Injeta uma mensagem user multimodal APÓS o tool message — é o
                // formato aceito por OpenAI/OpenRouter para visão em tool calls.
                var imageParts = sendScreenshotImages
                    ? AiChatHelpers.BuildImagePartsFromToolResult(wrapped, tr.Name ?? string.Empty)
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

            // P2.11: canal de gravação local indisponível. Injeta a regra no
            // MESMO turno (o prompt do sistema é reconstruído a cada round, mas
            // esta nota fica colada ao tool result que originou a falha) e
            // encerra a cadeia: sem isso o modelo pedia outra mutação e o
            // usuário esperava mais um round inteiro para receber o mesmo erro.
            if (localWriteUnavailable)
            {
                budgetExhausted = true;
                llmMessages.Add(new LlmMessage("system", AiChatHelpers.LocalAgentWriteUnavailableNote));
                _logger.LogWarning(
                    "[{TraceId}] Canal de gravação local indisponível (retryable=false) — nota anti-retry injetada e loop de ferramentas encerrado para SessionId={SessionId}",
                    traceId, session.Id);
            }

            try { await _messageRepository.CreateBatchAsync(toolMsgs, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir tool results", traceId); }

            // FIX-3: LIVELOCK. Quando o agent reenvia um lote cujos call_ids já
            // foram TODOS respondidos no histórico, o modelo reemite as mesmas
            // chamadas e o turno nunca converge (no incidente: 3 rounds com o
            // MESMO lote de 8 tools; com o meta/muse-ember cada round terminava
            // em 400). O dedupe B8-r2 descarta item a item e o orquestrador
            // seguia pedindo ao modelo; aqui o turno é encerrado com uma
            // mensagem acionável em vez de repetir o ciclo.
            if (AiChatHelpers.IsFullyRepeatedToolBatch(
                    toolResults.Select(tr => tr.CallId).ToList(), answeredCallIds))
            {
                _logger.LogWarning(
                    "[{TraceId}] Lote de tool results INTEIRAMENTE já respondido — encerrando o turno para não repetir o ciclo. CallIds: {CallIds}",
                    traceId, string.Join(", ", toolResults.Select(tr => tr.CallId)));
                yield return new AiChatStreamChunk(Type: "error",
                    Error: "Os resultados de ferramenta enviados já haviam sido processados. Reformule o pedido para continuar.");
                yield break;
            }

            // FIX-3 (P0): o payload ACABOU de ser montado (histórico normalizado em
            // BuildLlmMessagesFromHistory + tool results anexados acima). Renormaliza AQUI, no ponto do
            // append, porque a invariante do provedor vale sobre o que vai ao
            // wire — e era exatamente esta a lacuna do incidente de 2026-11-12:
            // os tool results entravam DEPOIS da normalização e o provedor
            // estrito (meta/muse-ember) devolvia 400 "Duplicate
            // function_call_output" / "No function call found for function call
            // output". O dedupe B8-r2 acima é por call_id e não reordena nem
            // remove respostas órfãs deixadas pelo corte de janela; o
            // normalizador faz as duas coisas.
            var beforeNormalize = llmMessages.Count;
            llmMessages = AiChatToolOrchestrator.NormalizeToolCallPairs(llmMessages);
            if (llmMessages.Count != beforeNormalize)
            {
                _logger.LogInformation(
                    "[{TraceId}] Payload do round renormalizado após o append dos tool results — {Before} -> {After} mensagens",
                    traceId, beforeNormalize, llmMessages.Count);
            }
        }

        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null) { yield return new AiChatStreamChunk(Type: "error", Error: "Agent não encontrado."); yield break; }

        // B13: usa a última mensagem do usuário do histórico (o 1º toolResult
        // pode ser ruído e degradava o contexto do prompt).
        var lastUserMessage = history.LastOrDefault(m => m.Role == "user")?.Content;
        // O seed do prompt (RAG/embedding) nunca pode ser um tool result com
        // base64 de imagem: Shorten limita o custo e evita embutir MBs.
        var promptSeed = message ?? lastUserMessage ?? AiChatCapabilityResolver.Shorten(toolResults?.FirstOrDefault()?.Result, 2000) ?? "";
        var (systemPrompt, _) = await _promptBuilder.BuildAsync(agent, session,
            promptSeed, aiSettings, departmentId, ct,
            await _capabilities.IsA2uiEnabledAsync(session.ClientId, session.SiteId, agentId, ct));

        var kbTools = aiSettings.KnowledgeBaseEnabled
            ? await _mcpToolExecutor.GetAvailableToolsAsync(session.ClientId, session.SiteId, agentId, ct)
            : new List<LlmTool>();
        var agentTools = await _toolOrchestrator.GetAgentToolsForScopeAsync(agentId, session.ClientId, session.SiteId, ct);
        var availableTools = AiChatToolOrchestrator.ApplyToolPromptOverrides(
            AiChatToolOrchestrator.MergeDistinctTools(kbTools, agentTools),
            aiSettings.ToolPromptOverrides);

        // FIX-3: guarda de capacidade de TOOLS (espelha a de visão). Um modelo
        // sem function calling recebia todos os schemas e o provedor devolvia 400
        // em cada round; sem tools o turno ainda entrega uma resposta em texto.
        if (availableTools.Count > 0
            && !await _capabilities.ShouldSendToolsAsync(aiSettings, session.ClientId, session.SiteId, ct))
        {
            _logger.LogWarning(
                "[{TraceId}] Modelo {Model} não declara suporte a tools — {Count} schema(s) NÃO serão enviados",
                traceId, aiSettings.ChatModel, availableTools.Count);
            availableTools = new List<LlmTool>();
        }

        var maxIterations = AiChatHelpers.ResolveMaxToolIterations(aiSettings);

        // Round do TURNO (não do request): ToolResults presentes = continuação de
        // uma tool chain já iniciada (round anterior + 1); mensagem nova do usuário
        // sem ToolResults = novo turno (= 1). A chave na cache sobrevive entre os
        // requests do mesmo turno — antes o contador local zerava e o heartbeat
        // mostrava sempre "round 1", e o orçamento configurado era ignorado no
        // caminho delegado ao agent.
        // Correção 2026-10-08: clique em surface A2UI é TURNO NOVO, não
        // continuação da tool chain anterior (ver AiChatTurnRound). Sem isso,
        // cada clique herdava os rounds do turno anterior e, com orçamento baixo,
        // o tool_call era abortado e a resposta sintetizada SEM tools — o
        // upgrade_package clicado nunca chegava ao agent (caso YogaDNS 4/3).
        var turnRound = AiChatTurnRound.Resolve(_memoryCache, session.Id,
            AiChatTurnRound.ResolveContinuesTurn(toolResults?.Select(tr => tr.Name)));
        // true quando o orçamento estourou e o modelo vai PERGUNTAR ao usuário se
        // pode continuar (renovação) em vez de sintetizar uma resposta final.
        bool continuationRequested = false;

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

        // ── Renovação de orçamento (continuação autorizada pelo usuário) ──
        // Um turno NOVO (mensagem do usuário, sem ToolResults) reinicia o contador
        // de rounds — é assim que o orçamento é renovado com o MESMO
        // MaxToolCallIterations configurado. Se havia ações pendentes porque o
        // orçamento do turno anterior esgotou, injeta a instrução para retomá-las;
        // sem isso o modelo responderia como se nada tivesse ficado para trás.
        if (toolResults is not { Count: > 0 })
        {
            var renewalKey = AiChatConstants.BudgetRenewalKey(session.Id);
            if (_memoryCache.TryGetValue(renewalKey, out List<string>? renewalPending) && renewalPending is not null)
            {
                _memoryCache.Remove(renewalKey);
                llmMessages.Add(new LlmMessage("system", AiChatHelpers.BuildBudgetRenewalResumeNote(renewalPending)));
                _logger.LogInformation(
                    "[{TraceId}] Orçamento renovado: retomando {Count} ação(ões) pendente(s) após resposta do usuário",
                    traceId, renewalPending.Count);
                yield return new AiChatStreamChunk(
                    Type: "budget_renewed",
                    Content: "Orçamento de rounds renovado — retomando as ações pendentes.",
                    LoopMaxRounds: maxIterations);
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
        var agentToolTimeouts = AiChatToolRateLimiter.BuildToolTimeouts(agentToolPolicies, agentToolCallNames);
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

            // FIX-3: teto de tools por round (58 schemas iam em TODOS os rounds).
            var uncappedCount = roundTools.Count;
            roundTools = AiChatHelpers.CapToolsPerRound(roundTools);
            if (roundTools.Count != uncappedCount)
            {
                _logger.LogInformation(
                    "[{TraceId}] Tools do round limitadas pelo teto de {Cap} — {Before} -> {After} (essenciais preservadas: {Kept})",
                    traceId, AiChatHelpers.MaxToolsPerRound, uncappedCount, roundTools.Count,
                    string.Join(", ", AiChatHelpers.AlwaysKeptToolNames));
            }

            // FIX-3: usa o maxTokens RESOLVIDO (ResolveChatMaxTokensAsync) —
            // antes o round usava ClampMaxTokens e a síntese o resolvido, então o
            // teto de saída mudava no meio do turno.
            var streamOptions = new LlmOptions(
                maxTokens, AiChatHelpers.ClampTemperature(aiSettings),
                string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                roundTools.Count > 0, roundTools, aiSettings.Provider,
                aiSettings.OpenRouterReferer, aiSettings.OpenRouterTitle, aiSettings.OpenRouterCategories,
                SessionId: session.Id.ToString("D"),
                TimeoutMs: AiChatHelpers.ClampAiTimeoutMs(aiSettings),
                // A7: amostragem configurável
                TopP: AiChatHelpers.ClampTopP(aiSettings),
                FrequencyPenalty: AiChatHelpers.ClampPenalty(aiSettings.FrequencyPenalty),
                PresencePenalty: AiChatHelpers.ClampPenalty(aiSettings.PresencePenalty),
                Seed: aiSettings.Seed,
                ResponseFormat: aiSettings.ResponseFormat,
                ReasoningEnabled: aiSettings.ReasoningEnabled, ReasoningEffort: aiSettings.ReasoningEffort);

            hasToolCalls = false;
            bool hasAgentToolCall = false;
            // Calls do agent suprimidas por orçamento esgotado: fechadas com
            // tool message sintético para o round de síntese não receber 400.
            var agentToolCallsPending = new List<LlmAssistantToolCall>();
            // P2.11: o round cujo PRÓPRIO tool call voltou com falha local
            // não-retentável é o último. A nota anti-retry já foi injetada no
            // bloco de tool results (que roda antes do while); aqui só
            // interrompemos a cadeia, deixando o round de síntese responder.
            var localWriteStop = false;
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
                                        traceId, tc.Name, errCount, errorJson, AiChatCapabilityResolver.Shorten(tc.ArgumentsJson, 300));
                                    if (errCount >= AiChatToolRateLimiter.MaxConsecutiveAgentToolArgErrors)
                                    {
                                        // B16: encerra o loop de tools sem injetar fallback no
                                        // buffer (que nunca era transmitido). A síntese forçada
                                        // abaixo tenta uma resposta real.
                                        _logger.LogWarning("[{TraceId}] Tool do agent {ToolName} rejeitada {Count}x por argumentos inválidos. Args={Args}. Encerrando loop de tools e sintetizando resposta.",
                                            traceId, tc.Name, errCount, AiChatCapabilityResolver.Shorten(tc.ArgumentsJson, 300));
                                        contentBuilder.Clear();
                                        hasToolCalls = false;
                                        goto streamMultiRoundDone;
                                    }
                                    llmMessages.Add(new LlmMessage("tool", errorJson!, tc.Id, tc.Name));
                                    continue;
                                }
                                var rateError = await _rateLimiter.CheckAgentToolRateLimitAsync(
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
                                    if (!string.IsNullOrEmpty(kbQuery) && !executedKbQueries.Add(kbQuery))
                                    {
                                        llmMessages.Add(new LlmMessage("tool", """{"found":false,"message":"Busca já realizada neste turno. Use as informações já obtidas."}""", tc.Id, tc.Name));
                                        continue;
                                    }
                                }
                                var toolResult = await _mcpToolExecutor.ExecuteAsync(tc.Name, tc.ArgumentsJson,
                                    session.ClientId, session.SiteId, agentId, aiSettings, null, departmentId, session.Id, ct);
                                if (AiChatToolOrchestrator.IsNonRetryableLocalAgentWrite(toolResult))
                                {
                                    localWriteStop = true;
                                    _logger.LogWarning(
                                        "[{TraceId}] Tool {ToolName} falhou por indisponibilidade LOCAL do canal de gravação (retryable=false) — encerrando o loop de ferramentas do turno",
                                        traceId, tc.Name);
                                }
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

                        // P2.11: canal de gravação local indisponível — o tool
                        // message já foi adicionado no foreach (pareamento
                        // tool_call/tool_message preservado) e a nota anti-retry
                        // já está no contexto. Encerra a cadeia deste turno: o
                        // modelo responde SEM tools, com o diagnóstico correto.
                        // Sem este corte o LLM pedia outra mutação e o usuário
                        // esperava outro round só para ver o mesmo erro.
                        if (localWriteStop)
                        {
                            budgetExhausted = true;
                            _logger.LogWarning(
                                "[{TraceId}] Loop de ferramentas encerrado no round {Round}: canal de gravação local indisponível (retryable=false)",
                                traceId, turnRound);
                            break;
                        }

                        if (hasAgentToolCall)
                        {
                            // Orçamento do turno esgotado: este request iria delegar
                            // outra tool ao agent, mas o máximo configurado já foi
                            // alcançado. Em vez de sintetizar uma resposta final (o
                            // usuário via "não consegui executar" sem erro real), o
                            // modelo PERGUNTA se pode continuar e as ações abortadas
                            // ficam registradas; a autorização abre um turno novo,
                            // que reinicia o contador de rounds = orçamento renovado
                            // com o MESMO MaxToolCallIterations.
                            if (AiChatTurnRound.IsBudgetExhausted(turnRound, maxIterations))
                            {
                                budgetExhausted = true;
                                hasAgentToolCall = false;
                                // O check usa turnRound >= maxIterations: com max=1
                                // NENHUMA tool é delegada, nem no round 1. Pedir
                                // renovação nesse caso criaria um ciclo infinito de
                                // "posso continuar?" sem executar nada — mantém a
                                // síntese e o limite fica explícito.
                                continuationRequested = maxIterations > 1;
                                var pendingSummary = continuationRequested
                                    ? AiChatHelpers.BuildPendingActionSummary(
                                        agentToolCallsPending.Select(p => (p.Name, (string?)p.ArgumentsJson)))
                                    : [];
                                if (pendingSummary.Count > 0)
                                {
                                    _memoryCache.Set(
                                        AiChatConstants.BudgetRenewalKey(session.Id),
                                        pendingSummary,
                                        AiChatConstants.BudgetRenewalTtl);
                                }
                                // Fecha as tool_calls já presentes em llmMessages (a
                                // delegação ao agent foi abortada) — sem isso o round
                                // de síntese leva 400 do provedor. O resultado deixa
                                // claro que NÃO houve falha: aguarda autorização.
                                var closingResult = continuationRequested
                                    ? AiChatHelpers.AgentToolBudgetAwaitingConsentResult
                                    : AiChatHelpers.AgentToolBudgetExhaustedResult;
                                foreach (var pendingCall in agentToolCallsPending)
                                    llmMessages.Add(new LlmMessage("tool", closingResult, pendingCall.Id, pendingCall.Name));
                                llmMessages.Add(new LlmMessage("system", continuationRequested
                                    ? AiChatHelpers.AgentBudgetContinuationNote
                                    : AiChatHelpers.SynthesisBudgetNote));
                                if (continuationRequested)
                                {
                                    _logger.LogInformation(
                                        "[{TraceId}] Orçamento de rounds do turno esgotado ({Round}/{Max}) com {Pending} ação(ões) pendente(s) — perguntando ao usuário se pode renovar",
                                        traceId, turnRound, maxIterations, pendingSummary.Count);
                                    // Evento dedicado: o agent mostra no chat "limite
                                    // atingido — aguardando autorização" em vez de
                                    // deixar o usuário sem explicação.
                                    yield return new AiChatStreamChunk(
                                        Type: "budget_exhausted",
                                        Content: $"Limite de rounds de ferramentas atingido ({turnRound}/{maxIterations}). " +
                                                 "As ações pendentes ainda NÃO foram executadas — aguardando sua autorização para continuar.",
                                        LoopRound: turnRound,
                                        LoopMaxRounds: maxIterations);
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "[{TraceId}] Orçamento de rounds esgotado ({Round}/{Max}) e MaxToolCallIterations={Max} não permite delegar nenhuma tool — sintetizando sem tools; ajuste em Configuração do Servidor › Integração com IA",
                                        traceId, turnRound, maxIterations, maxIterations);
                                }
                                // Sai do await foreach: o while reentra com roundTools
                                // vazio (sem chamada ao LLM aqui); a pergunta ao
                                // usuário sai do round de síntese pós-loop.
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
                                    Content = roundPreamble,
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

            // budgetExhausted: garante que o while rode o round sem tools mesmo
            // com o orçamento local atingido. No multi-round esse round NÃO faz
            // chamada ao LLM (não existe else para roundTools vazio); ele só
            // deixa o loop encerrar e a síntese forçada pós-loop responde.
            if (!hasToolCalls || (!budgetExhausted && toolIterations >= maxIterations)) break;
            toolIterations++;
        }

        // B16-r2: erro estruturado do provider — encerra o turno com error
        // (o controller repassa ao agent) em vez de seguir para a síntese.
        if (providerError is not null)
        {
            if (_costControl is not null && costAcquired && !anyTokenYielded && !hasToolCalls)
            {
                try { await _costControl.ReleaseAsync(session.ClientId, session.SiteId, aiSettings, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao liberar slot no CostControl (multi-round)", traceId); }
            }
            _logger.LogError("[{TraceId}] Provider stream error (multi-round): {Error}", traceId, providerError);
            yield return new AiChatStreamChunk(Type: "error", Error: providerError);
            yield break;
        }

    streamMultiRoundDone:
        stopwatch.Stop();
        var fullContent = contentBuilder.ToString();

        if (_costControl is not null)
        {
            if (totalTokens is > 0)
            {
                try { await _costControl.RecordUsageAsync(session.ClientId, session.SiteId, totalTokens.Value, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao registrar uso de tokens no CostControl (multi-round)", traceId); }
            }
            else if (costAcquired && !string.IsNullOrEmpty(providerError) && !anyTokenYielded && !hasToolCalls)
            {
                try { await _costControl.ReleaseAsync(session.ClientId, session.SiteId, aiSettings, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao liberar slot no CostControl (multi-round)", traceId); }
            }
        }

        fullContent = AiChatGuardrails.ApplyOutputGuardrails(fullContent, aiSettings);

        // ── A2UI + sanitização de vazamentos (mesma ordem única do StreamAsync) ──
        // Roda ANTES da síntese forçada: se o conteúdo era só vazamento (DSML,
        // invokes textuais), a sanitização o esvazia e a síntese dispara com a
        // nota correta. A extração A2UI vem PRIMEIRO para o sanitizador não
        // apagar o bloco ```a2ui antes de ele virar chunk "a2ui".
        var (cleanMultiContent, a2uiMultiMessages, multiWasSanitized) = AiChatOutputPipeline.Process(
            fullContent,
            reason => _logger.LogWarning("[{TraceId}] A2UI (multi-round) — {Reason}", traceId, reason));
        if (multiWasSanitized)
        {
            _logger.LogInformation("[{TraceId}] Vazamentos de tool call removidos do output multi-round ({OrigLen} -> {CleanLen} chars)",
                traceId, fullContent.Length, cleanMultiContent.Length);
        }
        fullContent = cleanMultiContent;
        if (!await _capabilities.IsA2uiEnabledAsync(session.ClientId, session.SiteId, agentId, ct))
        {
            if (a2uiMultiMessages.Count > 0)
                _logger.LogWarning("[{TraceId}] A2UI desabilitada na governanca (multi-round): {Count} mensagem(ns) descartada(s)", traceId, a2uiMultiMessages.Count);
        }
        else
        {
            // M2 (revisão 2026-10-08): mesma recuperação do round 1. Aqui o
            // systemPrompt já foi montado no início do método, então a reemissão
            // vale também para cards criados DEPOIS de uma tool round.
            var multiMissing = AiChatA2uiExtractor.SurfacesMissingDefinition(a2uiMultiMessages);
            if (multiMissing.Count > 0)
            {
                _logger.LogWarning("[{TraceId}] A2UI (multi-round) sem definição para a(s) surface(s) {Surfaces} — solicitando reemissão ao LLM",
                    traceId, string.Join(", ", multiMissing));
                var (retryContent, retryTokens) = await _capabilities.TryReemitA2uiDefinitionAsync(
                    systemPrompt, llmMessages, aiSettings, maxTokens, session.Id,
                    contentBuilder.ToString(), multiMissing, ct);
                if (retryTokens > 0)
                    totalTokens = (totalTokens ?? 0) + retryTokens;
                if (!string.IsNullOrWhiteSpace(retryContent))
                {
                    var (retryClean, retryMessages, retrySanitized) = AiChatOutputPipeline.Process(
                        retryContent,
                        reason => _logger.LogWarning("[{TraceId}] A2UI (multi-round, reemissão) — {Reason}", traceId, reason));
                    if (retrySanitized)
                    {
                        _logger.LogInformation("[{TraceId}] Vazamentos de tool call removidos da reemissão A2UI multi-round ({OrigLen} -> {CleanLen} chars)",
                            traceId, retryContent.Length, retryClean.Length);
                    }
                    if (retryMessages.Count > 0 && AiChatA2uiExtractor.SurfacesMissingDefinition(retryMessages).Count == 0)
                    {
                        a2uiMultiMessages = retryMessages;
                        if (string.IsNullOrWhiteSpace(fullContent) && !string.IsNullOrWhiteSpace(retryClean))
                            fullContent = retryClean;
                    }
                }
            }
            foreach (var a2uiMsg in a2uiMultiMessages)
            {
                yield return new AiChatStreamChunk(Type: "a2ui", A2uiJson: a2uiMsg);
            }
            // Diagnóstico DEPOIS das mensagens (mesmo contrato do round 1).
            if (AiChatA2uiExtractor.BuildIncompleteDiagnostic(a2uiMultiMessages) is { } stillMissingMulti)
                yield return new AiChatStreamChunk(Type: "a2ui_incomplete", Content: stillMissingMulti);
        }

        // ── Síntese forçada: conteúdo vazio (ou orçamento estourado) → chamadas
        // sem tools até obter resposta ──
        // budgetExhausted entra na condição de propósito: aqui um round sem
        // tools NÃO chama o LLM dentro do while, então a resposta final sai
        // daqui. Se o modelo tivesse deixado texto residual depois do
        // tool_calls suprimido, depender só de "conteúdo vazio" encerraria o
        // turno sem resposta.
        if ((string.IsNullOrWhiteSpace(fullContent) && a2uiMultiMessages.Count == 0) || budgetExhausted || continuationRequested)
        {
            // budgetExhausted: a nota já foi injetada no caminho delegado.
            if (continuationRequested)
            {
                // Conteúdo vazio: AgentBudgetContinuationNote (injetada no caminho
                // delegado) já instrui a pergunta.
                // Com texto JÁ emitido, o round de síntese não rodaria (o while
                // abaixo exigia conteúdo vazio) e o usuário nunca veria a pergunta
                // de autorização — injeta a variante que só acrescenta a pergunta.
                if (!string.IsNullOrWhiteSpace(fullContent))
                    llmMessages.Add(new LlmMessage("system", AiChatHelpers.AgentBudgetContinuationFollowUpNote));
                // A nota de "resposta vazia" NÃO entra: ela empurra o modelo a
                // responder qualquer coisa em vez de perguntar se pode renovar.
            }
            else if (hasToolCalls && !budgetExhausted && toolIterations >= maxIterations)
                llmMessages.Add(new LlmMessage("system", AiChatHelpers.SynthesisBudgetNote));
            else
                llmMessages.Add(new LlmMessage("user", AiChatHelpers.EmptyContentNote));

            for (var attempt = 1; attempt <= AiChatConstants.MaxSynthesisRetries
                 && (string.IsNullOrWhiteSpace(fullContent) || continuationRequested); attempt++)
            {
                // Com texto já emitido NÃO limpa o buffer: a pergunta é acrescentada
                // ao que o usuário já viu (limpar apagaria o turno).
                if (string.IsNullOrWhiteSpace(fullContent)) contentBuilder.Clear();
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
                    TopP: AiChatHelpers.ClampTopP(aiSettings),
                    FrequencyPenalty: AiChatHelpers.ClampPenalty(aiSettings.FrequencyPenalty),
                    PresencePenalty: AiChatHelpers.ClampPenalty(aiSettings.PresencePenalty),
                    Seed: aiSettings.Seed,
                    ResponseFormat: aiSettings.ResponseFormat,
                    ReasoningEnabled: aiSettings.ReasoningEnabled,
                    ReasoningEffort: string.IsNullOrWhiteSpace(aiSettings.ReasoningEffort) ? "low" : aiSettings.ReasoningEffort);
                var synthFilter = new AiChatStreamFilter(aiSettings);
                await foreach (var token in _llmProvider.StreamAsync(systemPrompt, llmMessages, synthesisOptions, ct))
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
                fullContent = contentBuilder.ToString();
                // A pergunta de autorização é uma única chamada: não repete em loop.
                if (continuationRequested) break;
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

    private sealed class CaseInsensitiveTupleComparer : IEqualityComparer<(string Id, string Name)>
    {
        public bool Equals((string Id, string Name) x, (string Id, string Name) y) =>
            string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Id, string Name) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name ?? string.Empty));
    }
}

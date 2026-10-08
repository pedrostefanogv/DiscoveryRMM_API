using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services.Ai;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Orquestrador principal do chat IA — delega responsabilidades específicas para sub-orquestradores:
/// - AiChatSystemPromptBuilder: construção de system prompts + RAG
/// - AiChatToolOrchestrator: tool calling, validação, XML fallback
/// - AiChatQuickReply: cache de respostas rápidas
/// - AiChatStreamingOrchestrator: streaming SSE com tool call loop
/// - AiChatGuardrails: validação de input e sanitização de output
/// </summary>
public class AiChatService : IAiChatService
{
    private readonly IAiChatSessionRepository _sessionRepository;
    private readonly IAiChatMessageRepository _messageRepository;
    private readonly IAiChatJobRepository _jobRepository;
    private readonly IAiChatJobQueue _jobQueue;
    private readonly ILlmProvider _llmProvider;
    private readonly IAgentRepository _agentRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly ILoggingService _loggingService;
    private readonly ILogger<AiChatService> _logger;
    private readonly IMcpToolExecutor _mcpToolExecutor;
    private readonly IAiCostControlService _costControl;
    private readonly IAiTokenBudgetResolver _tokenBudgetResolver;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly IAiCredentialResolver _credentialResolver;
    private readonly AiChatSystemPromptBuilder _promptBuilder;
    private readonly AiChatToolOrchestrator _toolOrchestrator;
    private readonly AiChatQuickReply _quickReply;
    private readonly AiChatStreamingOrchestrator _streamingOrchestrator;

    public AiChatService(
        IAiChatSessionRepository sessionRepository,
        IAiChatMessageRepository messageRepository,
        IAiChatJobRepository jobRepository,
        IAiChatJobQueue jobQueue,
        ILlmProvider llmProvider,
        IAgentRepository agentRepository,
        ISiteRepository siteRepository,
        ILoggingService loggingService,
        ILogger<AiChatService> logger,
        IMcpToolExecutor mcpToolExecutor,
        IAiCostControlService costControl,
        IAiTokenBudgetResolver tokenBudgetResolver,
        IConfigurationResolver configurationResolver,
        IAiCredentialResolver credentialResolver,
        AiChatSystemPromptBuilder promptBuilder,
        AiChatToolOrchestrator toolOrchestrator,
        AiChatQuickReply quickReply,
        AiChatStreamingOrchestrator streamingOrchestrator)
    {
        _sessionRepository = sessionRepository;
        _messageRepository = messageRepository;
        _jobRepository = jobRepository;
        _jobQueue = jobQueue;
        _llmProvider = llmProvider;
        _agentRepository = agentRepository;
        _siteRepository = siteRepository;
        _loggingService = loggingService;
        _logger = logger;
        _mcpToolExecutor = mcpToolExecutor;
        _costControl = costControl;
        _tokenBudgetResolver = tokenBudgetResolver;
        _configurationResolver = configurationResolver;
        _credentialResolver = credentialResolver;
        _promptBuilder = promptBuilder;
        _toolOrchestrator = toolOrchestrator;
        _quickReply = quickReply;
        _streamingOrchestrator = streamingOrchestrator;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ProcessSyncAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<AgentChatSyncResponse> ProcessSyncAsync(
        Guid agentId, string message, Guid? sessionId,
        string? createdByIp = null, int? requestMaxTokens = null,
        Guid? departmentId = null, CancellationToken ct = default)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var startTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        // Rastreia se TryAcquireAsync consumiu um slot de rate limit — se a
        // requisição falhar antes de consumir o LLM, o slot é devolvido no catch.
        var costControlAcquired = false;
        Guid scopeClientIdRelease = Guid.Empty;
        Guid scopeSiteIdRelease = Guid.Empty;
        AIIntegrationSettings? settingsRelease = null;

        try
        {
            _logger.LogInformation("[{TraceId}] ProcessSyncAsync iniciado para AgentId={AgentId}", traceId, agentId);
            AiChatGuardrails.ValidateUserInput(message, AiChatConstants.MaxMessageSizeBytes);

            var agent = await _agentRepository.GetByIdAsync(agentId) ?? throw new ArgumentException($"Agent {agentId} não encontrado");
            var site = await _siteRepository.GetByIdAsync(agent.SiteId) ?? throw new ArgumentException($"Site {agent.SiteId} não encontrado");
            var scopeSiteId = agent.SiteId;
            var scopeClientId = site.ClientId;
            var aiSettings = await ResolveAiSettingsAsync(scopeSiteId, ct);

            if (!aiSettings.Enabled || !aiSettings.ChatAIEnabled)
                throw new InvalidOperationException("Chat IA está desabilitado para este escopo.");

            if (aiSettings.CostControlEnabled)
            {
                if (!await _costControl.TryAcquireAsync(scopeClientId, scopeSiteId, aiSettings, ct))
                    throw new InvalidOperationException("Limite de uso de IA excedido.");
                costControlAcquired = true;
                scopeClientIdRelease = scopeClientId;
                scopeSiteIdRelease = scopeSiteId;
                settingsRelease = aiSettings;
            }

            var session = await GetOrCreateSessionAsync(sessionId, agentId, scopeSiteId, scopeClientId, startTime, traceId, createdByIp, ct);
            var historyMessages = await _messageRepository.GetRecentBySessionAsync(session.Id, AiChatHelpers.ClampHistoryMessages(aiSettings), ct);
            var nextSeq = historyMessages.Any() ? historyMessages.Max(m => m.SequenceNumber) + 1 : 1;

            var (systemPrompt, injectedArticleIds) = await _promptBuilder.BuildAsync(agent, session, message, aiSettings, departmentId, ct);
            var llmMessages = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(historyMessages);
            llmMessages.Add(new LlmMessage("user", message));

            var availableTools = await BuildAvailableToolsAsync(scopeClientId, scopeSiteId, agentId, aiSettings, ct);
            var maxIterations = AiChatHelpers.ResolveMaxToolIterations(aiSettings);
            // Orçamento model-aware também no chat síncrono: o pedido explícito do
            // cliente é limitado pela capacidade real do modelo e pelo teto do
            // produto (antes havia um clamp fixo de 8000 que rebaixava o pedido).
            var requestedMaxTokens = requestMaxTokens.HasValue
                ? Math.Clamp(requestMaxTokens.Value, 100, AiChatConstants.MaxOutputTokensCeiling)
                : AiChatHelpers.ClampMaxTokens(aiSettings);

            var clampedMaxTokens = (await _tokenBudgetResolver.ResolveForSiteAsync(
                scopeSiteId, requestedMaxTokens, null, ct)).MaxOutputTokens;

            var llmOptions = new LlmOptions(clampedMaxTokens, AiChatHelpers.ClampTemperature(aiSettings),
                aiSettings.ChatModel, aiSettings.BaseUrl, aiSettings.ApiKey,
                availableTools.Count > 0, availableTools, aiSettings.Provider,
                aiSettings.OpenRouterReferer, aiSettings.OpenRouterTitle, aiSettings.OpenRouterCategories,
                SessionId: session.Id.ToString("D"),
                TimeoutMs: AiChatHelpers.ClampAiTimeoutMs(aiSettings),
                    TopP: AiChatHelpers.ClampTopP(aiSettings),
                    FrequencyPenalty: AiChatHelpers.ClampPenalty(aiSettings.FrequencyPenalty),
                    PresencePenalty: AiChatHelpers.ClampPenalty(aiSettings.PresencePenalty),
                    Seed: aiSettings.Seed, ResponseFormat: aiSettings.ResponseFormat);

            LlmResponse llmResponse;
            var toolIterations = 0;
            while (true)
            {
                llmResponse = await _llmProvider.CompleteAsync(systemPrompt, llmMessages, llmOptions, ct);
                if (llmResponse.ToolCalls == null || llmResponse.ToolCalls.Count == 0 || toolIterations >= maxIterations) break;
                toolIterations++;

                var assistantToolCalls = llmResponse.ToolCalls.Select(tc => new LlmAssistantToolCall(tc.Id, tc.Name, tc.ArgumentsJson)).ToList();
                llmMessages.Add(new LlmMessage("assistant", llmResponse.Content ?? string.Empty, ToolCalls: assistantToolCalls));

                foreach (var tc in llmResponse.ToolCalls)
                {
                    var toolResult = await _mcpToolExecutor.ExecuteAsync(tc.Name, tc.ArgumentsJson, scopeClientId, scopeSiteId, agentId, aiSettings, injectedArticleIds, departmentId, session.Id, ct);
                    await _messageRepository.CreateAsync(new AiChatMessage { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq++, Role = "tool", Content = toolResult, ToolCallId = tc.Id, ToolName = tc.Name, CreatedAt = DateTime.UtcNow, TraceId = traceId }, ct);
                    llmMessages.Add(new LlmMessage("tool", toolResult, tc.Id, tc.Name));
                }
            }

            // ── Síntese forçada (agent loop resiliente) ──
            // Conteúdo vazio (ou orçamento esgotado com tool calls pendentes e
            // sem texto) → chamadas finais SEM tools até obter resposta.
            var budgetExhausted = llmResponse.ToolCalls is { Count: > 0 } && toolIterations >= maxIterations;
            if (string.IsNullOrWhiteSpace(llmResponse.Content))
            {
                llmMessages.Add(new LlmMessage("system", budgetExhausted
                    ? AiChatHelpers.SynthesisBudgetNote
                    : AiChatHelpers.EmptyContentNote));
                var synthesisOptions = llmOptions with { EnableTools = false, Tools = null };
                for (var attempt = 1; attempt <= AiChatConstants.MaxSynthesisRetries && string.IsNullOrWhiteSpace(llmResponse.Content); attempt++)
                {
                    llmResponse = await _llmProvider.CompleteAsync(systemPrompt, llmMessages, synthesisOptions, ct);
                    if (!string.IsNullOrWhiteSpace(llmResponse.Content)) break;
                }
            }

            stopwatch.Stop();
            if (aiSettings.CostControlEnabled) await _costControl.RecordUsageAsync(scopeClientId, scopeSiteId, llmResponse.TokensUsed, ct);

            var safeContent = AiChatGuardrails.ApplyOutputGuardrails(llmResponse.Content, aiSettings);

            // Extração A2UI + sanitização de vazamentos na MESMA ordem do
            // streaming (AiChatOutputPipeline): o LLM pode ter emitido tool calls
            // como TEXTO (DSML, blocos json com invokes) e/ou um bloco ```a2ui.
            // Sem a extração aqui, a interface era removida pela sanitização e
            // sumia em silêncio neste caminho (o endpoint sync não tem SSE para
            // emitir chunks "a2ui").
            var (cleanSync, a2uiSyncMessages, syncWasSanitized) = AiChatOutputPipeline.Process(
                safeContent,
                reason => _logger.LogWarning("[{TraceId}] A2UI descartada no sync (renderer rejeitaria): {Reason}", traceId, reason));
            if (syncWasSanitized)
            {
                _logger.LogInformation("[{TraceId}] Vazamentos de tool call removidos do output sync ({OrigLen} -> {CleanLen} chars)",
                    traceId, safeContent.Length, cleanSync.Length);
            }
            safeContent = cleanSync;
            if (string.IsNullOrWhiteSpace(safeContent))
            {
                // Resposta que era SÓ a interface A2UI (sem texto): a mensagem
                // de falha seria enganosa ao lado de um card válido.
                safeContent = a2uiSyncMessages.Count > 0
                    ? "Interface gerada — veja o card abaixo."
                    : "Não consegui concluir a ação solicitada. Tente reformular o pedido.";
            }

            await _messageRepository.CreateBatchAsync([
                new() { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq, Role = "user", Content = message, CreatedAt = startTime, TraceId = traceId },
                new() { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq + 1, Role = "assistant", Content = safeContent, TokensUsed = llmResponse.TokensUsed, LatencyMs = (int)stopwatch.ElapsedMilliseconds, ModelVersion = llmResponse.ModelVersion, CreatedAt = DateTime.UtcNow, TraceId = traceId }
            ], ct);

            var conversationTokens = await CalculateConversationTokens(session.Id, ct);
            await LogChatAsync(agentId, agent.SiteId, scopeClientId, session.Id, nextSeq, llmResponse, stopwatch, traceId, ct);

            return new AgentChatSyncResponse(
                session.Id,
                safeContent,
                llmResponse.TokensUsed,
                conversationTokens,
                (int)stopwatch.ElapsedMilliseconds,
                a2uiSyncMessages.Count > 0 ? a2uiSyncMessages : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{TraceId}] Erro em ProcessSyncAsync AgentId={AgentId}", traceId, agentId);

            // Devolve o slot de rate limit se a requisição falhou antes de
            // consumir o LLM (erro de rede, LLM indisponível, etc.) — evita
            // que erros transientes consumam o quota do usuário.
            if (costControlAcquired && settingsRelease is not null)
            {
                try
                {
                    await _costControl.ReleaseAsync(scopeClientIdRelease, scopeSiteIdRelease, settingsRelease, ct);
                    _logger.LogInformation("[{TraceId}] Slot de rate limit devolvido (falha antes do LLM)", traceId);
                }
                catch (Exception releaseEx)
                {
                    _logger.LogWarning(releaseEx, "[{TraceId}] Falha ao devolver slot de rate limit", traceId);
                }
            }

            await _loggingService.LogExceptionAsync(ex, LogType.AiChat, LogSource.Api, $"Erro chat sync AgentId={agentId}", new { SessionId = sessionId, Message = message }, agentId: agentId.ToString(), cancellationToken: ct);
            throw;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ProcessAsyncAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<Guid> ProcessAsyncAsync(Guid agentId, string message, Guid? sessionId, int? requestMaxTokens = null, Guid? departmentId = null, CancellationToken ct = default)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        try
        {
            AiChatGuardrails.ValidateUserInput(message, AiChatConstants.MaxMessageSizeBytes);
            var agent = await _agentRepository.GetByIdAsync(agentId) ?? throw new ArgumentException($"Agent {agentId} não encontrado");
            var site = await _siteRepository.GetByIdAsync(agent.SiteId) ?? throw new ArgumentException($"Site {agent.SiteId} não encontrado");

            var session = sessionId.HasValue
                ? await _sessionRepository.GetByIdAsync(sessionId.Value, agentId, ct) ?? throw new ArgumentException($"Sessão {sessionId} não encontrada")
                : await _sessionRepository.CreateAsync(new AiChatSession { Id = Guid.NewGuid(), AgentId = agentId, SiteId = agent.SiteId, ClientId = site.ClientId, Topic = "general", CreatedAt = DateTime.UtcNow, CreatedByIp = "unknown", TraceId = traceId, ExpiresAt = DateTime.UtcNow.AddDays(AiChatConstants.SessionExpirationDays) }, ct);

            var job = new AiChatJob { Id = Guid.NewGuid(), SessionId = session.Id, AgentId = agentId, Status = "Pending", UserMessage = message, CreatedAt = DateTime.UtcNow, TraceId = traceId };
            await _jobRepository.CreateAsync(job, ct);
            await _loggingService.LogInfoAsync(LogType.AiChat, LogSource.Api, $"Job assíncrono criado: JobId={job.Id}", new { JobId = job.Id, SessionId = session.Id }, agentId: agentId.ToString(), siteId: agent.SiteId.ToString(), cancellationToken: ct);
            await _jobQueue.EnqueueAsync(job.Id, agentId, ct);
            return job.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{TraceId}] Erro ProcessAsyncAsync AgentId={AgentId}", traceId, agentId);
            await _loggingService.LogExceptionAsync(ex, LogType.AiChat, LogSource.Api, $"Erro job assíncrono AgentId={agentId}", new { SessionId = sessionId, Message = message }, agentId: agentId.ToString(), cancellationToken: ct);
            throw;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GetJobStatusAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<AgentChatJobStatus> GetJobStatusAsync(Guid jobId, Guid agentId, CancellationToken ct)
    {
        var job = await _jobRepository.GetByIdAsync(jobId, agentId, ct) ?? throw new ArgumentException($"Job {jobId} não encontrado");
        return new AgentChatJobStatus(job.Id, job.Status, job.SessionId, job.AssistantMessage, job.TokensUsed, job.ErrorMessage, job.CreatedAt, job.CompletedAt);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // StreamAsync — delega para AiChatStreamingOrchestrator
    // ══════════════════════════════════════════════════════════════════════════

    public async IAsyncEnumerable<AiChatStreamChunk> StreamAsync(Guid agentId, string message, Guid? sessionId, Guid? departmentId = null, string? systemNote = null, List<string>? images = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in _streamingOrchestrator.StreamAsync(agentId, message, sessionId, ResolveAiSettingsAsync, departmentId, systemNote, images, ct))
            yield return chunk;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ProcessTicketPromptAsync
    // ══════════════════════════════════════════════════════════════════════════

    public Task<LlmResponse> ProcessTicketPromptAsync(string systemPrompt, string userMessage, Guid siteId, int maxTokens, double temperature, Guid? departmentId = null, CancellationToken ct = default)
        => ExecuteTicketPromptAsync(systemPrompt, userMessage, siteId, maxTokens, temperature, null, ct);

    /// <summary>
    /// Igual a <see cref="ProcessTicketPromptAsync"/>, mas permite exigir saída
    /// estruturada (response_format). Usado pela triagem de atribuição por IA.
    /// </summary>
    public Task<LlmResponse> ProcessTicketPromptJsonAsync(
        string systemPrompt, string userMessage, Guid siteId, int maxTokens, double temperature,
        string? responseFormat, Guid? departmentId = null, CancellationToken ct = default)
        => ExecuteTicketPromptAsync(systemPrompt, userMessage, siteId, maxTokens, temperature, responseFormat, ct);

    /// <summary>
    /// Execução única dos prompts de ticket: resolve credenciais/escopo, aplica o
    /// teto de tokens efetivo e respeita rate limit + budget diário de IA
    /// (IAiCostControlService). O bloqueio vira <see cref="AiUsageLimitException"/>,
    /// que os chamadores tratam como degradação para fallback — nunca erro 500.
    /// </summary>
    private async Task<LlmResponse> ExecuteTicketPromptAsync(
        string systemPrompt, string userMessage, Guid siteId, int maxTokens, double temperature,
        string? responseFormat, CancellationToken ct)
    {
        var (aiSettings, clientId) = await ResolveScopeAsync(siteId, ct);
        if (!aiSettings.Enabled || string.IsNullOrWhiteSpace(aiSettings.ApiKey)) throw new InvalidOperationException("IA não configurada.");
        if (!aiSettings.ChatAIEnabled) throw new InvalidOperationException("Chat IA desabilitado.");

        // Orçamento de tokens AUTOMÁTICO para todos os fluxos de IA de ticket:
        // menor entre o pedido, a configuração do tenant, a capacidade real do
        // modelo (catálogo, cacheado) e o teto do produto. Antes o teto do modelo
        // só era aplicado na triagem; os demais fluxos podiam passar do limite.
        var budget = await _tokenBudgetResolver.ResolveAsync(
            siteId, clientId, aiSettings, maxTokens, null, ct);
        var effectiveMaxTokens = Math.Clamp(
            budget.MaxOutputTokens,
            AiTokenLimits.MinimumOutputTokens,
            AiChatConstants.MaxOutputTokensCeiling);

        var scopeClientId = clientId ?? Guid.Empty;
        var acquired = false;
        if (aiSettings.CostControlEnabled)
        {
            acquired = await _costControl.TryAcquireAsync(scopeClientId, siteId, aiSettings, ct);
            if (!acquired)
            {
                throw new AiUsageLimitException(
                    AiUsageLimitReason.RateOrBudget,
                    "rate limit ou budget diário de IA atingido");
            }
        }

        var options = new LlmOptions(
            effectiveMaxTokens,
            temperature,
            aiSettings.ChatModel,
            aiSettings.BaseUrl,
            aiSettings.ApiKey,
            Provider: aiSettings.Provider,
            TimeoutMs: aiSettings.TimeoutMs,
            TopP: AiChatHelpers.ClampTopP(aiSettings),
            FrequencyPenalty: AiChatHelpers.ClampPenalty(aiSettings.FrequencyPenalty),
            PresencePenalty: AiChatHelpers.ClampPenalty(aiSettings.PresencePenalty),
            Seed: aiSettings.Seed,
            ResponseFormat: responseFormat ?? aiSettings.ResponseFormat,
            ReasoningEnabled: aiSettings.ReasoningEnabled,
            ReasoningEffort: aiSettings.ReasoningEffort);

        try
        {
            var response = await _llmProvider.CompleteAsync(
                systemPrompt, [new LlmMessage("user", userMessage)], options, ct);

            if (acquired && response.TokensUsed > 0)
                await _costControl.RecordUsageAsync(scopeClientId, siteId, response.TokensUsed, ct);

            return response;
        }
        catch (AiUsageLimitException)
        {
            throw;
        }
        catch
        {
            // Devolve o slot quando a chamada falhou antes de consumir o LLM.
            if (acquired)
                await _costControl.ReleaseAsync(scopeClientId, siteId, aiSettings, ct);
            throw;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // RegisterAgentToolsAsync — delega para AiChatToolOrchestrator
    // ══════════════════════════════════════════════════════════════════════════

    public async Task RegisterAgentToolsAsync(Guid agentId, Guid siteId, List<AgentToolRegistration> tools, CancellationToken ct = default)
        => await _toolOrchestrator.RegisterAgentToolsAsync(agentId, siteId, tools, ct);

    // ══════════════════════════════════════════════════════════════════════════
    // StreamMultiRoundAsync — delega para AiChatStreamingOrchestrator
    // ══════════════════════════════════════════════════════════════════════════

    public async IAsyncEnumerable<AiChatStreamChunk> StreamMultiRoundAsync(Guid agentId, string? message, Guid? sessionId, List<ToolResultItem>? toolResults, Guid? departmentId = null, string? systemNote = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in _streamingOrchestrator.StreamMultiRoundAsync(agentId, message, sessionId, toolResults, ResolveAiSettingsAsync, departmentId, systemNote, ct))
            yield return chunk;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Private helpers
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<AiChatSession> GetOrCreateSessionAsync(Guid? sessionId, Guid agentId, Guid scopeSiteId, Guid scopeClientId, DateTime startTime, string traceId, string? createdByIp, CancellationToken ct)
    {
        if (sessionId.HasValue)
            return await _sessionRepository.GetByIdAsync(sessionId.Value, agentId, ct) ?? throw new ArgumentException($"Sessão {sessionId} não encontrada");

        var session = await _sessionRepository.CreateAsync(new AiChatSession
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            SiteId = scopeSiteId,
            ClientId = scopeClientId,
            Topic = "general",
            CreatedAt = startTime,
            CreatedByIp = createdByIp ?? "unknown",
            TraceId = traceId,
            ExpiresAt = startTime.AddDays(AiChatConstants.SessionExpirationDays)
        }, ct);

        _logger.LogInformation("[{TraceId}] Nova sessão criada: SessionId={SessionId}", traceId, session.Id);
        return session;
    }

    private async Task<List<LlmTool>> BuildAvailableToolsAsync(Guid scopeClientId, Guid scopeSiteId, Guid agentId, AIIntegrationSettings aiSettings, CancellationToken ct)
    {
        var kbTools = aiSettings.KnowledgeBaseEnabled
            ? await _mcpToolExecutor.GetAvailableToolsAsync(scopeClientId, scopeSiteId, agentId, ct)
            : [];

        var agentTools = _toolOrchestrator.GetCachedAgentTools(agentId);
        // Dedupe por nome (KB + agent): função repetida no payload é ambígua.
        return AiChatToolOrchestrator.MergeDistinctTools(kbTools, agentTools);
    }

    private async Task<int> CalculateConversationTokens(Guid sessionId, CancellationToken ct)
    {
        var stats = await _messageRepository.GetStatsAsync(sessionId, ct);
        return stats.EstimatedTokens;
    }

    private async Task<AIIntegrationSettings> ResolveAiSettingsAsync(Guid siteId, CancellationToken ct)
        => (await ResolveScopeAsync(siteId, ct)).Settings;

    /// <summary>
    /// Resolve as configurações de IA e o cliente do escopo. O cliente é usado
    /// pelo cost control (rate limit/budget são por cliente+site).
    /// </summary>
    private async Task<(AIIntegrationSettings Settings, Guid? ClientId)> ResolveScopeAsync(
        Guid siteId, CancellationToken ct)
    {
        var resolved = await _configurationResolver.ResolveForSiteAsync(siteId);
        ct.ThrowIfCancellationRequested();
        var ai = resolved.AIIntegration ?? new AIIntegrationSettings();

        if (resolved.ClientId.HasValue)
        {
            var credential = await _credentialResolver.ResolveAsync(resolved.ClientId.Value, siteId, ct);
            if (credential is not null)
            {
                if (!string.IsNullOrWhiteSpace(credential.ApiKey)) ai.ApiKey = credential.ApiKey;
                if (!string.IsNullOrWhiteSpace(credential.BaseUrl)) ai.BaseUrl = credential.BaseUrl;
                if (!string.IsNullOrWhiteSpace(credential.EmbeddingBaseUrl)) ai.EmbeddingBaseUrl = credential.EmbeddingBaseUrl;
                if (!string.IsNullOrWhiteSpace(credential.EmbeddingApiKey)) ai.EmbeddingApiKey = credential.EmbeddingApiKey;
                if (!string.IsNullOrWhiteSpace(credential.Provider)) ai.Provider = credential.Provider;
            }
        }

        return (ai, resolved.ClientId);
    }

    private async Task LogChatAsync(Guid agentId, Guid siteId, Guid clientId, Guid sessionId, int nextSeq, LlmResponse llmResponse, Stopwatch sw, string traceId, CancellationToken ct)
    {
        await _loggingService.LogInfoAsync(LogType.AiChat, LogSource.Api, $"Chat sync processado AgentId={agentId}",
            new { SessionId = sessionId, MessageSequence = nextSeq, TokensUsed = llmResponse.TokensUsed, LatencyMs = sw.ElapsedMilliseconds, ModelVersion = llmResponse.ModelVersion },
            agentId: agentId.ToString(), siteId: siteId.ToString(), clientId: clientId.ToString(), cancellationToken: ct);
        _logger.LogInformation("[{TraceId}] ProcessSyncAsync concluído: Latency={LatencyMs}ms, Tokens={TokensUsed}", traceId, sw.ElapsedMilliseconds, llmResponse.TokensUsed);
    }
}

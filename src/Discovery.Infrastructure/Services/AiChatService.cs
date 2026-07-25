using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
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
            }

            var session = await GetOrCreateSessionAsync(sessionId, agentId, scopeSiteId, scopeClientId, startTime, traceId, createdByIp, ct);
            var historyMessages = await _messageRepository.GetRecentBySessionAsync(session.Id, AiChatHelpers.ClampHistoryMessages(aiSettings), ct);
            var nextSeq = historyMessages.Any() ? historyMessages.Max(m => m.SequenceNumber) + 1 : 1;

            var (systemPrompt, injectedArticleIds) = await _promptBuilder.BuildAsync(agent, session, message, aiSettings, departmentId, ct);
            var llmMessages = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(historyMessages);
            llmMessages.Add(new LlmMessage("user", message));

            var availableTools = await BuildAvailableToolsAsync(scopeClientId, scopeSiteId, agentId, aiSettings, ct);
            var maxIterations = aiSettings.MaxToolCallIterations is >= 1 and <= 10 ? aiSettings.MaxToolCallIterations : AiChatConstants.DefaultMaxToolCallIterations;
            var clampedMaxTokens = requestMaxTokens.HasValue ? Math.Clamp(requestMaxTokens.Value, 100, 8000) : AiChatHelpers.ClampMaxTokens(aiSettings);

            var llmOptions = new LlmOptions(clampedMaxTokens, AiChatHelpers.ClampTemperature(aiSettings),
                aiSettings.ChatModel, aiSettings.BaseUrl, aiSettings.ApiKey,
                availableTools.Count > 0, availableTools, aiSettings.Provider,
                aiSettings.OpenRouterReferer, aiSettings.OpenRouterTitle, aiSettings.OpenRouterCategories,
                SessionId: session.Id.ToString("D"));

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

            stopwatch.Stop();
            if (aiSettings.CostControlEnabled) await _costControl.RecordUsageAsync(scopeClientId, scopeSiteId, llmResponse.TokensUsed, ct);

            var safeContent = AiChatGuardrails.ApplyOutputGuardrails(llmResponse.Content, aiSettings);
            await _messageRepository.CreateBatchAsync([
                new() { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq, Role = "user", Content = message, CreatedAt = startTime, TraceId = traceId },
                new() { Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq + 1, Role = "assistant", Content = safeContent, TokensUsed = llmResponse.TokensUsed, LatencyMs = (int)stopwatch.ElapsedMilliseconds, ModelVersion = llmResponse.ModelVersion, CreatedAt = DateTime.UtcNow, TraceId = traceId }
            ], ct);

            var conversationTokens = await CalculateConversationTokens(session.Id, ct);
            await LogChatAsync(agentId, agent.SiteId, scopeClientId, session.Id, nextSeq, llmResponse, stopwatch, traceId, ct);

            return new AgentChatSyncResponse(session.Id, safeContent, llmResponse.TokensUsed, conversationTokens, (int)stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{TraceId}] Erro em ProcessSyncAsync AgentId={AgentId}", traceId, agentId);
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

    public async IAsyncEnumerable<AiChatStreamChunk> StreamAsync(Guid agentId, string message, Guid? sessionId, Guid? departmentId = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in _streamingOrchestrator.StreamAsync(agentId, message, sessionId, ResolveAiSettingsAsync, departmentId, ct))
            yield return chunk;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ProcessTicketPromptAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<LlmResponse> ProcessTicketPromptAsync(string systemPrompt, string userMessage, Guid siteId, int maxTokens, double temperature, Guid? departmentId = null, CancellationToken ct = default)
    {
        var aiSettings = await ResolveAiSettingsAsync(siteId, ct);
        if (!aiSettings.Enabled || string.IsNullOrWhiteSpace(aiSettings.ApiKey)) throw new InvalidOperationException("IA não configurada.");
        if (!aiSettings.ChatAIEnabled) throw new InvalidOperationException("Chat IA desabilitado.");

        return await _llmProvider.CompleteAsync(systemPrompt, [new LlmMessage("user", userMessage)], new LlmOptions(maxTokens, temperature, aiSettings.ChatModel, aiSettings.BaseUrl, aiSettings.ApiKey, Provider: aiSettings.Provider), ct);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // RegisterAgentToolsAsync — delega para AiChatToolOrchestrator
    // ══════════════════════════════════════════════════════════════════════════

    public async Task RegisterAgentToolsAsync(Guid agentId, Guid siteId, List<AgentToolRegistration> tools, CancellationToken ct = default)
        => await _toolOrchestrator.RegisterAgentToolsAsync(agentId, siteId, tools, ct);

    // ══════════════════════════════════════════════════════════════════════════
    // StreamMultiRoundAsync — delega para AiChatStreamingOrchestrator
    // ══════════════════════════════════════════════════════════════════════════

    public async IAsyncEnumerable<AiChatStreamChunk> StreamMultiRoundAsync(Guid agentId, string? message, Guid? sessionId, List<ToolResultItem>? toolResults, Guid? departmentId = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in _streamingOrchestrator.StreamMultiRoundAsync(agentId, message, sessionId, toolResults, ResolveAiSettingsAsync, departmentId, ct))
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
            Id = Guid.NewGuid(), AgentId = agentId, SiteId = scopeSiteId, ClientId = scopeClientId,
            Topic = "general", CreatedAt = startTime, CreatedByIp = createdByIp ?? "unknown",
            TraceId = traceId, ExpiresAt = startTime.AddDays(AiChatConstants.SessionExpirationDays)
        }, ct);

        _logger.LogInformation("[{TraceId}] Nova sessão criada: SessionId={SessionId}", traceId, session.Id);
        return session;
    }

    private async Task<List<LlmTool>> BuildAvailableToolsAsync(Guid scopeClientId, Guid scopeSiteId, Guid agentId, AIIntegrationSettings aiSettings, CancellationToken ct)
    {
        var tools = aiSettings.KnowledgeBaseEnabled
            ? await _mcpToolExecutor.GetAvailableToolsAsync(scopeClientId, scopeSiteId, agentId, ct)
            : [];

        var agentTools = _toolOrchestrator.GetCachedAgentTools(agentId);
        if (agentTools is { Count: > 0 }) tools.AddRange(agentTools);
        return tools;
    }

    private async Task<int> CalculateConversationTokens(Guid sessionId, CancellationToken ct)
    {
        var stats = await _messageRepository.GetStatsAsync(sessionId, ct);
        return stats.EstimatedTokens;
    }

    private async Task<AIIntegrationSettings> ResolveAiSettingsAsync(Guid siteId, CancellationToken ct)
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
        return ai;
    }

    private async Task LogChatAsync(Guid agentId, Guid siteId, Guid clientId, Guid sessionId, int nextSeq, LlmResponse llmResponse, Stopwatch sw, string traceId, CancellationToken ct)
    {
        await _loggingService.LogInfoAsync(LogType.AiChat, LogSource.Api, $"Chat sync processado AgentId={agentId}",
            new { SessionId = sessionId, MessageSequence = nextSeq, TokensUsed = llmResponse.TokensUsed, LatencyMs = sw.ElapsedMilliseconds, ModelVersion = llmResponse.ModelVersion },
            agentId: agentId.ToString(), siteId: siteId.ToString(), clientId: clientId.ToString(), cancellationToken: ct);
        _logger.LogInformation("[{TraceId}] ProcessSyncAsync concluído: Latency={LatencyMs}ms, Tokens={TokensUsed}", traceId, sw.ElapsedMilliseconds, llmResponse.TokensUsed);
    }
}

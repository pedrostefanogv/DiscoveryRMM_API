using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Serviço de chat IA integrado com agents do Discovery RMM
/// Orquestra chamadas OpenAI, gerencia histórico e processa tool calls MCP
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
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IKnowledgeChunkRepository _chunkRepository;
    private readonly IKnowledgeMcpTool _knowledgeMcpTool;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly IAiCredentialResolver _credentialResolver;
    private readonly IMcpToolExecutor _mcpToolExecutor;
    private readonly IAiCostControlService _costControl;
    
    // Cache de tools registradas por agent (para multi-round com tools do agent)
    private static readonly ConcurrentDictionary<Guid, List<LlmTool>> _agentToolsCache = new();
    private static readonly ConcurrentDictionary<Guid, DateTime> _agentToolsCacheExpiry = new();
    private static readonly TimeSpan AgentToolsCacheTtl = TimeSpan.FromHours(24);
    
    private const int MaxMessageSizeBytes = 2048; // 2KB
    private const int SessionExpirationDays = 180;
    private const int DefaultMaxToolCallIterations = 2;
    private const int DefaultMaxHistoryMessages = 20;
    private const int DefaultMaxKbContextTokens = 2000;
    private const int DefaultMaxTokens = 1000;
    private const double DefaultTemperature = 0.7;

    /// <summary>
    /// Regex para detectar tool calls em formato texto (XML-like) geradas por
    /// modelos que não suportam function calling nativo (ex: Llama via certos providers).
    /// Ex: &lt;knowledgesearch&gt;{"query":"teste","maxresults":5}&lt;/knowledge_search&gt;
    /// NOTA: abertura e fechamento podem ter nomes diferentes (com/sem underscore).
    /// </summary>
    private static readonly Regex XmlToolCallRegex = new(
        @"<(\w+)>\s*(\{[^}]*\})\s*</\w+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Mapeia aliases de nome de tool (ex: "knowledgesearch" → "knowledge_search").
    /// </summary>
    private static readonly Dictionary<string, string> XmlToolAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["knowledgesearch"] = "knowledge_search",
        ["searchknowledge"] = "knowledge_search",
        ["kbsearch"] = "knowledge_search",
        ["filesystemread"] = "filesystem.read_file",
        ["readfile"] = "filesystem.read_file",
        ["timecurrent"] = "time.current",
        ["gettime"] = "time.current",
        ["memorysearch"] = "memory.search",
        ["sequentialthinking"] = "sequential_thinking",
        ["postgresquery"] = "postgres.query",
    };
    
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
        IEmbeddingProvider embeddingProvider,
        IKnowledgeChunkRepository chunkRepository,
        IKnowledgeMcpTool knowledgeMcpTool,
        IConfigurationResolver configurationResolver,
        IAiCredentialResolver credentialResolver,
        IMcpToolExecutor mcpToolExecutor,
        IAiCostControlService costControl)
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
        _embeddingProvider = embeddingProvider;
        _chunkRepository = chunkRepository;
        _knowledgeMcpTool = knowledgeMcpTool;
        _configurationResolver = configurationResolver;
        _credentialResolver = credentialResolver;
        _mcpToolExecutor = mcpToolExecutor;
        _costControl = costControl;
    }
    
    /// <summary>
    /// Processa uma mensagem de chat síncrona (rápida)
    /// </summary>
    public async Task<AgentChatSyncResponse> ProcessSyncAsync(
        Guid agentId, 
        string message, 
        Guid? sessionId,
        string? createdByIp = null,
        int? requestMaxTokens = null,
        Guid? departmentId = null,
        CancellationToken ct = default)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var startTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            _logger.LogInformation(
                "[{TraceId}] ProcessSyncAsync iniciado para AgentId={AgentId}, SessionId={SessionId}",
                LogSanitizer.Sanitize(traceId), agentId.ToString("D"), LogSanitizer.Sanitize(sessionId?.ToString()));
            
            // 1. Validar input
            ValidateUserInput(message);
            
            // 2. Buscar agent e contexto
            var agent = await _agentRepository.GetByIdAsync(agentId);
            if (agent == null)
            {
                throw new ArgumentException($"Agent {agentId} não encontrado", nameof(agentId));
            }

            var site = await _siteRepository.GetByIdAsync(agent.SiteId);
            if (site == null)
            {
                throw new ArgumentException($"Site {agent.SiteId} não encontrado para Agent {agentId}", nameof(agentId));
            }

            var scopeSiteId = agent.SiteId;
            var scopeClientId = site.ClientId;
            var aiSettings = await ResolveAiSettingsAsync(scopeSiteId, ct);

            if (!aiSettings.Enabled || !aiSettings.ChatAIEnabled)
            {
                throw new InvalidOperationException("Chat IA está desabilitado para este escopo.");
            }

            // ── Cost control check ──
            if (aiSettings.CostControlEnabled)
            {
                var allowed = await _costControl.TryAcquireAsync(scopeClientId, scopeSiteId, aiSettings, ct);
                if (!allowed)
                {
                    throw new InvalidOperationException(
                        "Limite de uso de IA excedido. Tente novamente mais tarde ou contate o administrador.");
                }
            }
            
            // 3. Criar ou recuperar sessão
            AiChatSession session;
            if (sessionId.HasValue)
            {
                var existingSession = await _sessionRepository.GetByIdAsync(sessionId.Value, agentId, ct);
                if (existingSession == null)
                {
                    throw new ArgumentException(
                        $"Sessão {sessionId} não encontrada para AgentId {agentId}", 
                        nameof(sessionId));
                }
                session = existingSession;
            }
            else
            {
                // Nova sessão
                session = new AiChatSession
                {
                    Id = Guid.NewGuid(),
                    AgentId = agentId,
                    SiteId = scopeSiteId,
                    ClientId = scopeClientId,
                    Topic = "general",
                    CreatedAt = startTime,
                    CreatedByIp = createdByIp ?? "unknown",
                    TraceId = traceId,
                    ExpiresAt = startTime.AddDays(SessionExpirationDays)
                };
                
                session = await _sessionRepository.CreateAsync(session, ct);
                
                _logger.LogInformation(
                    "[{TraceId}] Nova sessão criada: SessionId={SessionId}",
                    traceId, session.Id);
            }
            
            // 4. Buscar histórico recente (últimas 10 mensagens)
            var historyMessages = await _messageRepository.GetRecentBySessionAsync(
                session.Id, 
                ClampHistoryMessages(aiSettings), 
                ct);
            
            // 5. Determinar próximo SequenceNumber
            var nextSequenceNumber = historyMessages.Any() 
                ? historyMessages.Max(m => m.SequenceNumber) + 1 
                : 1;
            
            // 6. Build system prompt com contexto do agent + RAG da KB
            var (systemPrompt, injectedArticleIds) = await BuildSystemPromptAsync(
                agent, session, message, aiSettings, departmentId, ct);
            
            // 7. Converter histórico para formato LLM
            var llmMessages = historyMessages
                .OrderBy(m => m.SequenceNumber)
                .Select(m => new LlmMessage(m.Role, m.Content, m.ToolCallId, m.ToolName))
                .ToList();
            
            // 8. Adicionar mensagem atual do usuário
            llmMessages.Add(new LlmMessage("user", message));
            
            // 9. Chamar LLM com tool call loop (MCP via McpToolExecutor)
            var availableTools = aiSettings.KnowledgeBaseEnabled
                ? await _mcpToolExecutor.GetAvailableToolsAsync(scopeClientId, scopeSiteId, agentId, ct)
                : [];

            // ── Mesclar agent tools do cache ──
            var agentTools = GetCachedAgentTools(agentId);
            if (agentTools is { Count: > 0 })
            {
                availableTools.AddRange(agentTools);
                _logger.LogDebug("[{TraceId}] ProcessSyncAsync: {Count} agent tools mescladas ao stream",
                    traceId, agentTools.Count);
            }
            else
            {
                _logger.LogWarning("[{TraceId}] ProcessSyncAsync: Nenhuma agent tool em cache para AgentId={AgentId}. " +
                    "O agent pode não ter registrado tools ou o cache expirou (TTL={Ttl}). " +
                    "Verifique se o endpoint /me/agent-tools/registry retorna 200.",
                    traceId, agentId, AgentToolsCacheTtl);
            }

            var maxIterations = aiSettings.MaxToolCallIterations is >= 1 and <= 10
                ? aiSettings.MaxToolCallIterations
                : DefaultMaxToolCallIterations;

            // Respeitar MaxTokens do request, com clamp
            var clampedMaxTokens = requestMaxTokens.HasValue
                ? Math.Clamp(requestMaxTokens.Value, 100, 8000)
                : ClampMaxTokens(aiSettings);

            var llmOptions = new LlmOptions(
                MaxTokens: clampedMaxTokens,
                Temperature: ClampTemperature(aiSettings),
                Model: string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                BaseUrl: string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                ApiKey: string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                EnableTools: availableTools.Count > 0,
                Tools: availableTools,
                Provider: aiSettings.Provider,
                OpenRouterReferer: aiSettings.OpenRouterReferer,
                OpenRouterTitle: aiSettings.OpenRouterTitle,
                OpenRouterCategories: aiSettings.OpenRouterCategories,
                SessionId: session.Id.ToString("D"));
            
            LlmResponse llmResponse;
            var toolIterations = 0;

            while (true)
            {
                llmResponse = await _llmProvider.CompleteAsync(
                    systemPrompt,
                    llmMessages,
                    llmOptions,
                    ct);

                // Se não há tool calls ou atingiu limite, encerra
                if (llmResponse.ToolCalls == null || llmResponse.ToolCalls.Count == 0 ||
                    toolIterations >= maxIterations)
                    break;

                toolIterations++;

                // Adiciona a resposta do assistant (com tool calls) ao contexto
                llmMessages.Add(new LlmMessage("assistant", llmResponse.Content ?? string.Empty));

                // Processa cada tool call via McpToolExecutor
                foreach (var toolCall in llmResponse.ToolCalls)
                {
                    var toolResult = await _mcpToolExecutor.ExecuteAsync(
                        toolCall.Name,
                        toolCall.ArgumentsJson,
                        scopeClientId,
                        scopeSiteId,
                        agentId,
                        aiSettings,
                        injectedArticleIds,
                        departmentId,
                        session.Id,
                        ct);

                    _logger.LogDebug("[{TraceId}] MCP tool '{ToolName}' executada ({Iter}/{Max})",
                        traceId, toolCall.Name, toolIterations, maxIterations);

                    // Persiste a mensagem da tool call e o resultado
                    await _messageRepository.CreateAsync(new AiChatMessage
                    {
                        Id = Guid.NewGuid(),
                        SessionId = session.Id,
                        SequenceNumber = nextSequenceNumber++,
                        Role = "tool",
                        Content = toolResult,
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        CreatedAt = DateTime.UtcNow,
                        TraceId = traceId
                    }, ct);

                    llmMessages.Add(new LlmMessage("tool", toolResult, toolCall.Id, toolCall.Name));
                }
            }
            
            stopwatch.Stop();
            
            // ── Record token usage for cost control ──
            if (aiSettings.CostControlEnabled)
            {
                await _costControl.RecordUsageAsync(scopeClientId, scopeSiteId, llmResponse.TokensUsed, ct);
            }

            // ── Apply output guardrails ──
            var safeContent = ApplyOutputGuardrails(llmResponse.Content, aiSettings);

            // 10. Persistir mensagem do usuário e assistant em lote (transação única)
            var userMessage = new AiChatMessage
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                SequenceNumber = nextSequenceNumber,
                Role = "user",
                Content = message,
                CreatedAt = startTime,
                TraceId = traceId
            };

            var assistantMessage = new AiChatMessage
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                SequenceNumber = nextSequenceNumber + 1,
                Role = "assistant",
                Content = safeContent,
                TokensUsed = llmResponse.TokensUsed,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                ModelVersion = llmResponse.ModelVersion,
                CreatedAt = DateTime.UtcNow,
                TraceId = traceId
            };

            await _messageRepository.CreateBatchAsync([userMessage, assistantMessage], ct);
            
            // 12. Calcular tokens totais da conversa
            var conversationTokens = await CalculateConversationTokens(session.Id, ct);
            
            // 13. Logging para auditoria
            await _loggingService.LogInfoAsync(
                LogType.AiChat,
                LogSource.Api,
                $"Chat sync processado para AgentId={agentId}",
                new
                {
                    SessionId = session.Id,
                    MessageSequence = nextSequenceNumber,
                    TokensUsed = llmResponse.TokensUsed,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    ModelVersion = llmResponse.ModelVersion
                },
                agentId: agentId.ToString(),
                siteId: agent.SiteId.ToString(),
                clientId: scopeClientId.ToString(),
                cancellationToken: ct
            );
            
            _logger.LogInformation(
                "[{TraceId}] ProcessSyncAsync concluído: Latency={LatencyMs}ms, Tokens={TokensUsed}",
                traceId, stopwatch.ElapsedMilliseconds, llmResponse.TokensUsed);
            
            // 14. Retornar resposta
            return new AgentChatSyncResponse(
                SessionId: session.Id,
                AssistantMessage: safeContent,
                TokensUsed: llmResponse.TokensUsed,
                ConversationTokensTotal: conversationTokens,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "[{TraceId}] Erro ao processar chat sync para AgentId={AgentId}: {Error}",
                traceId, agentId, ex.Message);
            
            await _loggingService.LogExceptionAsync(
                ex,
                LogType.AiChat,
                LogSource.Api,
                $"Erro ao processar chat sync para AgentId={agentId}",
                new { SessionId = sessionId, Message = message },
                agentId: agentId.ToString(),
                cancellationToken: ct
            );
            
            throw;
        }
    }
    
    /// <summary>
    /// Processa uma mensagem de chat assíncrona (longa)
    /// </summary>
    public async Task<Guid> ProcessAsyncAsync(
        Guid agentId, 
        string message, 
        Guid? sessionId,
        int? requestMaxTokens = null,
        Guid? departmentId = null,
        CancellationToken ct = default)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        
        try
        {
            _logger.LogInformation(
                "[{TraceId}] ProcessAsyncAsync iniciado para AgentId={AgentId}, SessionId={SessionId}",
                LogSanitizer.Sanitize(traceId), agentId.ToString("D"), LogSanitizer.Sanitize(sessionId?.ToString()));
            
            // 1. Validar input
            ValidateUserInput(message);
            
            // 2. Buscar agent
            var agent = await _agentRepository.GetByIdAsync(agentId);
            if (agent == null)
            {
                throw new ArgumentException($"Agent {agentId} não encontrado", nameof(agentId));
            }

            var site = await _siteRepository.GetByIdAsync(agent.SiteId);
            if (site == null)
            {
                throw new ArgumentException($"Site {agent.SiteId} não encontrado para Agent {agentId}", nameof(agentId));
            }
            
            // 3. Criar ou recuperar sessão
            AiChatSession session;
            if (sessionId.HasValue)
            {
                var existingSession = await _sessionRepository.GetByIdAsync(sessionId.Value, agentId, ct);
                if (existingSession == null)
                {
                    throw new ArgumentException(
                        $"Sessão {sessionId} não encontrada para AgentId {agentId}", 
                        nameof(sessionId));
                }
                session = existingSession;
            }
            else
            {
                // Nova sessão
                session = new AiChatSession
                {
                    Id = Guid.NewGuid(),
                    AgentId = agentId,
                    SiteId = agent.SiteId,
                    ClientId = site.ClientId,
                    Topic = "general",
                    CreatedAt = DateTime.UtcNow,
                    CreatedByIp = "unknown",
                    TraceId = traceId,
                    ExpiresAt = DateTime.UtcNow.AddDays(SessionExpirationDays)
                };
                
                session = await _sessionRepository.CreateAsync(session, ct);
            }
            
            // 4. Criar job com status Pending
            var job = new AiChatJob
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                AgentId = agentId,
                Status = "Pending",
                UserMessage = message,
                CreatedAt = DateTime.UtcNow,
                TraceId = traceId
            };
            
            await _jobRepository.CreateAsync(job, ct);
            
            // 5. Logging
            await _loggingService.LogInfoAsync(
                LogType.AiChat,
                LogSource.Api,
                $"Job assíncrono criado: JobId={job.Id}",
                new { JobId = job.Id, SessionId = session.Id },
                agentId: agentId.ToString(),
                siteId: agent.SiteId.ToString(),
                cancellationToken: ct
            );
            
            _logger.LogInformation(
                "[{TraceId}] Job assíncrono criado: JobId={JobId}",
                traceId, job.Id);

            await _jobQueue.EnqueueAsync(job.Id, agentId, ct);
            
            return job.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[{TraceId}] Erro ao criar job assíncrono para AgentId={AgentId}: {Error}",
                traceId, agentId, ex.Message);
            
            await _loggingService.LogExceptionAsync(
                ex,
                LogType.AiChat,
                LogSource.Api,
                $"Erro ao criar job assíncrono para AgentId={agentId}",
                new { SessionId = sessionId, Message = message },
                agentId: agentId.ToString(),
                cancellationToken: ct
            );
            
            throw;
        }
    }
    
    /// <summary>
    /// Consulta o status de um job assíncrono
    /// </summary>
    public async Task<AgentChatJobStatus> GetJobStatusAsync(
        Guid jobId, 
        Guid agentId, 
        CancellationToken ct)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        
        try
        {
            _logger.LogDebug(
                "[{TraceId}] GetJobStatusAsync: JobId={JobId}, AgentId={AgentId}",
                traceId, jobId, agentId);
            
            var job = await _jobRepository.GetByIdAsync(jobId, agentId, ct);
            if (job == null)
            {
                throw new ArgumentException(
                    $"Job {jobId} não encontrado para AgentId {agentId}", 
                    nameof(jobId));
            }
            
            return new AgentChatJobStatus(
                JobId: job.Id,
                Status: job.Status,
                SessionId: job.SessionId,
                AssistantMessage: job.AssistantMessage,
                TokensUsed: job.TokensUsed,
                ErrorMessage: job.ErrorMessage,
                CreatedAt: job.CreatedAt,
                CompletedAt: job.CompletedAt
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[{TraceId}] Erro ao consultar status do job: JobId={JobId}, AgentId={AgentId}",
                traceId, jobId, agentId);
            
            throw;
        }
    }
    
    /// <summary>
    /// Streaming SSE: retorna chunks incrementais enquanto o LLM gera tokens.
    /// Streaming SSE: retorna chunks incrementais com suporte a tool calls.
    /// Suporta loop de MCP tools (até MaxToolCallIterations) e RAG departamental.
    /// Persiste as mensagens no DB ao final do stream.
    /// </summary>
    public async IAsyncEnumerable<AiChatStreamChunk> StreamAsync(
        Guid agentId,
        string message,
        Guid? sessionId,
        Guid? departmentId = null,
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
        int maxIterations = DefaultMaxToolCallIterations;

        try
        {
            ValidateUserInput(message);

            var agent = await _agentRepository.GetByIdAsync(agentId);
            if (agent == null)
                throw new ArgumentException($"Agent {agentId} não encontrado");

            var site = await _siteRepository.GetByIdAsync(agent.SiteId);
            if (site == null)
                throw new ArgumentException($"Site {agent.SiteId} não encontrado");

            scopeSiteId = agent.SiteId;
            scopeClientId = site.ClientId;
            aiSettings = await ResolveAiSettingsAsync(agent.SiteId, ct);

            if (!aiSettings.Enabled || !aiSettings.ChatAIEnabled)
                throw new InvalidOperationException("Chat IA está desabilitado para este escopo.");

            maxIterations = aiSettings.MaxToolCallIterations is >= 1 and <= 10
                ? aiSettings.MaxToolCallIterations
                : DefaultMaxToolCallIterations;

            if (sessionId.HasValue)
            {
                var existing = await _sessionRepository.GetByIdAsync(sessionId.Value, agentId, ct);
                session = existing ?? throw new ArgumentException($"Sessão {sessionId} não encontrada");
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
                    ExpiresAt = startTime.AddDays(SessionExpirationDays)
                }, ct);
            }

            var history = await _messageRepository.GetRecentBySessionAsync(
                session.Id, ClampHistoryMessages(aiSettings), ct);

            nextSeq = history.Any() ? history.Max(m => m.SequenceNumber) + 1 : 1;

            // RAG com departmentId (se fornecido, libera artigos Internal do departamento)
            (systemPrompt, _) = await BuildSystemPromptAsync(
                agent, session, message, aiSettings, departmentId, ct);

            llmMessages = history
                .OrderBy(m => m.SequenceNumber)
                .Select(m => new LlmMessage(m.Role, m.Content, m.ToolCallId, m.ToolName))
                .ToList();
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

        // Quick reply para mensagens curtas e comuns sem histórico
        // Só aplica se for sessão nova (sessionId era null) — sem histórico no DB
        if (!sessionId.HasValue)
        {
            var quickReply = TryQuickReply(message, null);
            if (quickReply != null)
            {
                await PersistQuickReplyAsync(session.Id, message, quickReply, nextSeq, startTime, traceId, aiSettings, stopwatch, ct);
                yield return new AiChatStreamChunk(Type: "token", Content: quickReply);
                yield return new AiChatStreamChunk(Type: "done", SessionId: session.Id, LatencyMs: (int)stopwatch.ElapsedMilliseconds);
                yield break;
            }
        }

        // ── Streaming com tool call loop ──────────────────────────────────────
        var contentBuilder = new StringBuilder();
        var toolIterations = 0;
        var injectedArticleIds = new List<Guid>();
        int? totalTokens = null;
        var toolMessagesToPersist = new List<AiChatMessage>();

        // Deduplica chamadas knowledge_search na mesma sessão
        var executedKbQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consecutiveEmptyKbSearches = 0;
        bool hasToolCalls = false;

        // Tools disponíveis no escopo
        var availableTools = aiSettings.KnowledgeBaseEnabled
            ? await _mcpToolExecutor.GetAvailableToolsAsync(scopeClientId, scopeSiteId, agentId, ct)
            : [];

        // ── Mesclar agent tools do cache ──
        var agentTools = GetCachedAgentTools(agentId);
        if (agentTools is { Count: > 0 })
        {
            availableTools.AddRange(agentTools);
            _logger.LogDebug("[{TraceId}] StreamAsync: {Count} agent tools mescladas ao stream",
                traceId, agentTools.Count);
        }
        else
        {
            _logger.LogWarning("[{TraceId}] StreamAsync: Nenhuma agent tool em cache para AgentId={AgentId}. " +
                "O agent pode não ter registrado tools ou o cache expirou (TTL={Ttl}). " +
                "Verifique se o endpoint /me/agent-tools/registry retorna 200.",
                traceId, agentId, AgentToolsCacheTtl);
        }

        // Nomes das tools do agent (para delegar ao invés de executar no servidor)
        var agentToolCallNames = new HashSet<string>(agentTools?.Select(at => at.Name) ?? [], StringComparer.OrdinalIgnoreCase);
        bool hasAgentToolCallPending = false;

        while (true)
        {
            var streamOptions = new LlmOptions(
                MaxTokens: ClampMaxTokens(aiSettings),
                Temperature: ClampTemperature(aiSettings),
                Model: string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                BaseUrl: string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                ApiKey: string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                EnableTools: availableTools.Count > 0,
                Tools: availableTools,
                Provider: aiSettings.Provider,
                OpenRouterReferer: aiSettings.OpenRouterReferer,
                OpenRouterTitle: aiSettings.OpenRouterTitle,
                OpenRouterCategories: aiSettings.OpenRouterCategories,
                SessionId: session!.Id.ToString("D"));

            hasToolCalls = false;
            hasAgentToolCallPending = false;

            if (availableTools.Count > 0)
            {
                // Stream com tool calls
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

                        // Adiciona resposta parcial do assistant ao contexto
                        llmMessages.Add(new LlmMessage("assistant", contentBuilder.ToString()));

                        foreach (var toolCall in evt.ToolCalls)
                        {
                            // ── Agent tools: delegar ao agent (multi-round) ──
                            if (agentToolCallNames.Contains(toolCall.Name))
                            {
                                hasAgentToolCallPending = true;
                                _logger.LogDebug("[{TraceId}] Agent tool '{ToolName}' delegada ao agent", traceId, toolCall.Name);
                                yield return new AiChatStreamChunk(Type: "tool_call",
                                    ToolCallId: toolCall.Id, ToolName: toolCall.Name, ToolArgumentsDelta: toolCall.ArgumentsJson);
                                // Não persiste aqui — o agent vai retornar o resultado no round 2
                                continue;
                            }

                            // Deduplica conhecimento: evita chamar knowledge_search com a mesma query
                            if (toolCall.Name == "knowledge_search")
                            {
                                var kbQuery = ExtractKbQuery(toolCall.ArgumentsJson);
                                if (!string.IsNullOrEmpty(kbQuery) && !executedKbQueries.Add(kbQuery))
                                {
                                    _logger.LogDebug("[{TraceId}] knowledge_search duplicada ignorada: '{Query}'", traceId, kbQuery);
                                    yield return new AiChatStreamChunk(
                                        Type: "tool_result",
                                        ToolCallId: toolCall.Id,
                                        ToolResult: """{"found":false,"message":"Busca já realizada sem resultados. Use seu conhecimento próprio."}""");
                                    llmMessages.Add(new LlmMessage("tool", """{"found":false,"message":"Busca já realizada sem resultados. Use seu conhecimento próprio."}""", toolCall.Id, toolCall.Name));
                                    continue;
                                }
                            }

                            yield return new AiChatStreamChunk(
                                Type: "tool_call_start",
                                ToolCallId: toolCall.Id,
                                ToolName: toolCall.Name);

                            var toolResult = await _mcpToolExecutor.ExecuteAsync(
                                toolCall.Name,
                                toolCall.ArgumentsJson,
                                scopeClientId,
                                scopeSiteId,
                                agentId,
                                aiSettings,
                                null,
                                departmentId,
                                session.Id,
                                ct);

                            // Rastreia resultados vazios consecutivos de knowledge_search
                            if (toolCall.Name == "knowledge_search" && toolResult.Contains("\"found\":false"))
                            {
                                consecutiveEmptyKbSearches++;
                                var kbQuery = ExtractKbQuery(toolCall.ArgumentsJson);
                                _logger.LogDebug("[{TraceId}] knowledge_search sem resultados para '{Query}' ({EmptyCount}/{MaxIter})", traceId, kbQuery, consecutiveEmptyKbSearches, maxIterations);
                            }

                            yield return new AiChatStreamChunk(
                                Type: "tool_result",
                                ToolCallId: toolCall.Id,
                                ToolResult: toolResult);

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

                            _logger.LogDebug("[{TraceId}] MCP tool '{ToolName}' executada via stream ({Iter}/{Max})",
                                traceId, toolCall.Name, toolIterations + 1, maxIterations);
                        }

                        // Se houve agent tool call, encerra o round para o agent executar
                        if (hasAgentToolCallPending)
                        {
                            yield return new AiChatStreamChunk(Type: "round_end", SessionId: session.Id);
                            // Persiste só o que foi acumulado até aqui, sem o conteúdo final
                            stopwatch.Stop();
                            try
                            {
                                await _messageRepository.CreateBatchAsync(new[]
                                {
                                    new AiChatMessage
                                    {
                                        Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq,
                                        Role = "user", Content = message, CreatedAt = startTime, TraceId = traceId
                                    }
                                }, ct);
                            }
                            catch (Exception ex) { _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir user message do round 1", traceId); }
                            yield break;
                        }
                    }
                    else if (evt.Type == "done")
                    {
                        totalTokens = evt.TokensUsed;
                    }
                }
            }
            else
            {
                // Stream sem tools (fallback simples)
                await foreach (var token in _llmProvider.StreamAsync(systemPrompt, llmMessages, streamOptions, ct))
                {
                    contentBuilder.Append(token);
                    yield return new AiChatStreamChunk(Type: "token", Content: token);
                }
            }

            if (!hasToolCalls || toolIterations >= maxIterations - 1)
                break;

            // Break early se knowledge_search retornou vazio 2x consecutivas — força resposta direta
            if (consecutiveEmptyKbSearches >= 2)
            {
                _logger.LogDebug("[{TraceId}] knowledge_search retornou vazio {EmptyCount}x consecutivas. Forçando resposta direta.", traceId, consecutiveEmptyKbSearches);
                break;
            }

            toolIterations++;
        }

        stopwatch.Stop();
        var fullContent = contentBuilder.ToString();

        // ── Fallback: parse XML tool calls no texto final ──
        // Só tenta XML fallback quando: (a) não houver tools disponíveis (modelo sem function calling)
        // ou (b) o modelo usou texto puro em vez de tool_calls nativos.
        // Para modelos com function calling nativo que já receberam tools, pula o XML fallback.
        var shouldTryXmlFallback = availableTools.Count == 0 || !hasToolCalls;
        if (shouldTryXmlFallback)
        {
            var (cleanedContent, updatedNextSeq) = await ParseAndExecuteXmlToolCallsAsync(
                fullContent, availableTools,
                scopeClientId, scopeSiteId, agentId,
                aiSettings, departmentId,
                llmMessages, toolMessagesToPersist,
                session.Id, nextSeq, traceId,
                ct);
            fullContent = cleanedContent;
            nextSeq = updatedNextSeq;
        }

        // ── Retry: resposta vazia após tool calls ──
        if (string.IsNullOrWhiteSpace(fullContent) && toolIterations > 0)
        {
            _logger.LogWarning("[{TraceId}] Resposta vazia após {ToolIter} iterações de tool calls. Tentando retry sem tools.", traceId, toolIterations);

            llmMessages.Add(new LlmMessage("user",
                "[SISTEMA] Você não forneceu uma resposta visível ao usuário. Forneça uma resposta direta e útil à última pergunta do usuário."));

            var retryOptions = new LlmOptions(
                MaxTokens: ClampMaxTokens(aiSettings),
                Temperature: ClampTemperature(aiSettings),
                Model: string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                BaseUrl: string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                ApiKey: string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                EnableTools: false,
                Tools: null,
                Provider: aiSettings.Provider,
                OpenRouterReferer: aiSettings.OpenRouterReferer,
                OpenRouterTitle: aiSettings.OpenRouterTitle,
                OpenRouterCategories: aiSettings.OpenRouterCategories,
                SessionId: session!.Id.ToString("D"));

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

        // ── Persistência pós-stream (lote transacional) ───────────────────────
        try
        {
            var messagesToCreate = new List<AiChatMessage>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    SequenceNumber = nextSeq++,
                    Role = "user",
                    Content = message,
                    CreatedAt = startTime,
                    TraceId = traceId
                },
                new()
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
                }
            };

            // Inclui tool messages no batch (re-sequenciadas corretamente)
            messagesToCreate.AddRange(toolMessagesToPersist);

            await _messageRepository.CreateBatchAsync(messagesToCreate, ct);

            _logger.LogInformation(
                "[{TraceId}] StreamAsync concluído: AgentId={AgentId}, ContentLen={Len}, Latency={LatencyMs}ms",
                traceId, agentId, fullContent.Length, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{TraceId}] Falha ao persistir mensagens do stream", traceId);
        }

        yield return new AiChatStreamChunk(
            Type: "done",
            SessionId: session.Id,
            LatencyMs: (int)stopwatch.ElapsedMilliseconds);
    }

    // ── Ticket Prompt (shared with TicketAiController) ────────────────────────

    /// <summary>
    /// Processa um prompt para contexto de ticket (triagem/resumo/sugestão), sem persistência de histórico.
    /// </summary>
    public async Task<LlmResponse> ProcessTicketPromptAsync(
        string systemPrompt,
        string userMessage,
        Guid siteId,
        int maxTokens,
        double temperature,
        Guid? departmentId = null,
        CancellationToken ct = default)
    {
        var aiSettings = await ResolveAiSettingsAsync(siteId, ct);

        if (!aiSettings.Enabled || string.IsNullOrWhiteSpace(aiSettings.ApiKey))
            throw new InvalidOperationException("IA não configurada para este escopo.");

        if (!aiSettings.ChatAIEnabled)
            throw new InvalidOperationException("Chat IA está desabilitado para este escopo.");

        var llmOptions = new LlmOptions(
            MaxTokens: maxTokens,
            Temperature: temperature,
            Model: string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
            BaseUrl: string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
            ApiKey: string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
            Provider: aiSettings.Provider);

        return await _llmProvider.CompleteAsync(
            systemPrompt,
            [new LlmMessage("user", userMessage)],
            llmOptions,
            ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Rejeita: vazio, > 2KB, padrões maliciosos
    /// </summary>
    private void ValidateUserInput(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Mensagem não pode ser vazia", nameof(message));
        }
        
        var sizeBytes = System.Text.Encoding.UTF8.GetByteCount(message);
        if (sizeBytes > MaxMessageSizeBytes)
        {
            throw new ArgumentException(
                $"Mensagem excede o limite de {MaxMessageSizeBytes} bytes (atual: {sizeBytes} bytes)", 
                nameof(message));
        }
        
        // Detectar padrões maliciosos (XSS, script injection)
        var maliciousPatterns = new[]
        {
            @"<script[^>]*>",
            @"javascript:",
            @"eval\s*\(",
            @"on\w+\s*=",  // onclick=, onerror=, etc
            @"<iframe[^>]*>",
            @"<object[^>]*>",
            @"<embed[^>]*>"
        };
        
        foreach (var pattern in maliciousPatterns)
        {
            if (Regex.IsMatch(message, pattern, RegexOptions.IgnoreCase))
            {
                throw new ArgumentException(
                    "Mensagem contém padrões não permitidos", 
                    nameof(message));
            }
        }
    }
    
    /// <summary>
    /// Constrói o system prompt com contexto do agent
    /// Inclui: AgentId, Hostname, OS, Site, Client
    /// </summary>
    private static string BuildDefaultSystemPrompt(Agent agent)
    {
        return $@"Você é um assistente técnico especializado em suporte de TI, atua como suporte de primeiro nível, deve ajudar e orientar o usuário em relação às dúvidas mais comuns no dia a dia em relação à informática. Se necessário, utilize a base de conhecimento para saber mais sobre determinados assuntos internos. Tente ajudar os usuários a resolver os problemas mais comuns e, caso não consiga, abra um chamado para que possa ser verificado posteriormente.

**Contexto do Agent:**
- AgentId: {agent.Id}
- Hostname: {agent.Hostname}
- Sistema Operacional: {agent.OperatingSystem ?? "Desconhecido"} {agent.OsVersion ?? ""}
- Site: {agent.SiteId} 
- Status: {agent.Status}
- Último IP: {agent.LastIpAddress ?? "Desconhecido"}
- Última comunicação: {agent.LastSeenAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Desconhecido"}

**Ferramentas disponíveis no servidor:**
- `knowledge_search`: Pesquisa artigos e procedimentos na base de conhecimento da empresa.
- `time.current`: Retorna data/hora atual.
- `sequential_thinking`: Raciocínio multi-step para diagnósticos complexos.
- `memory.search`: Pesquisa informações salvas em conversas anteriores.

**Ferramentas do agente (executadas no computador do usuário):**
{{AGENT_TOOLS_SECTION}}

**Suas responsabilidades:**
1. Fornecer suporte técnico claro e direto
2. Diagnosticar problemas de forma sistemática
3. Sugerir soluções práticas e bem fundamentadas
4. Priorizar a segurança e estabilidade do sistema
5. Explicar conceitos técnicos de forma acessível

**Diretrizes para uso de ferramentas:**
- Use SEMPRE function calls JSON nativas para invocar ferramentas. NUNCA escreva tags XML como <tool> ou <function>.
- Ao chamar uma ferramenta, preencha TODOS os parâmetros obrigatórios com valores extraídos da conversa. Se o usuário disse ""Quero instalar o Foxit Reader"", o parâmetro `query` deve ser ""Foxit Reader"", NUNCA vazio. Isso vale para QUALQUER ferramenta: `search_packages`, `install_package`, `knowledge_search`, `ask_user`, `create_ticket`, etc.
- Se uma ferramenta retornar erro de parâmetro faltando (ex: ""nao pode ser vazio"", ""é obrigatório"", ""parameter missing""), NÃO pergunte ao usuário de novo — RELEIA a mensagem do usuário no histórico e extraia o valor correto. O usuário JÁ forneceu a informação.
- Se knowledge_search retornar sem resultados (`found: false`), NÃO chame novamente com a mesma query. Responda com seu conhecimento próprio. Se o problema não puder ser resolvido, oriente o usuário a abrir um chamado de suporte.
- Se o usuário pedir uma ação para a qual você tem ferramenta, USE a ferramenta. Não ofereça passos manuais se pode executar automaticamente.
- Se tiver dúvidas sobre algo que não está claro, pergunte ao usuário — mas evite perguntas repetitivas. Se a informação já foi fornecida antes, use-a.

**🟢 ABERTURA DE CHAMADO (FLUXO OBRIGATÓRIO):**
Quando o usuário pedir para abrir um chamado/ticket, OU quando você não conseguir resolver o problema com as ferramentas disponíveis, siga este fluxo EXATO:

1️⃣ **Extraia os dados da conversa**: leia o histórico e identifique:
   - O problema relatado (ex: ""Não consigo abrir PDF"", ""Computador lento"", etc.)
   - O que o usuário já tentou ou precisa (ex: ""Quero instalar o Foxit Reader"", ""Preciso de ajuda para configurar VPN"", etc.)
   - O hostname e SO do agente (já fornecidos no contexto acima)

2️⃣ **Monte o chamado como sugestão** usando os dados extraídos — NÃO pergunte ""qual o problema?"" se o usuário já disse:
   - **Título**: resuma o problema em uma frase (ex: ""Instalação do Foxit Reader no DESKTOP-JLO3IKQ"", ""VPN não conecta no Windows 10 do LAPTOP-1234"")
   - **Descrição**: junte tudo que o usuário relatou + contexto do sistema + o que já foi tentado
   - **Categoria**: deduza da conversa (Software, Hardware, Rede, etc.)
   - **Prioridade**: baseie-se na urgência aparente, faça uma avaliação razoável (Alta, Média, Baixa) de acordo com o impacto do problema e o contexto do usuário. Se não houver urgência aparente, use ""Baixa"".

3️⃣ **Apresente a sugestão ao usuário** para confirmar:
   ""Montei o chamado com essas informações:
   - Título: ...
   - Descrição: ...
   - Categoria: ...
   - Prioridade: ...
   Está correto? Quer ajustar algo ou posso criar?""

4️⃣ **Após confirmação**, chame a ferramenta `create_ticket` com os dados confirmados.

5️⃣ Se o usuário pedir ajustes, modifique APENAS o que ele mencionou e reapresente.

**IMPORTANTE**: NUNCA entre em loop de perguntas. Se o usuário já descreveu o problema, USE essa descrição. Não pergunte ""qual o problema?"" repetidamente. Se a informação não estiver clara, pergunte uma única vez de forma direta.

**🧠 MEMÓRIA DO USUÁRIO E MÁQUINA:**
- Ao INICIAR cada conversa, SEMPRE consulte a memória (`memory.search`) para ver anotações de interações anteriores com este usuário/máquina.
- SALVE na memória (`memory.save`) fatos importantes sobre o usuário e a máquina, como:
  - Nome do usuário e preferências do mesmo
  - Problemas recorrentes e soluções aplicadas
  - Qualquer informação que possa ser útil em conversas futuras
- Use essas informações para personalizar o atendimento e compreender melhor o contexto do usuário e suas preferências.

**Diretrizes gerais:**
- Seja conciso e objetivo. Respostas longas são aceitáveis apenas quando o problema for complexo.
- Oriente o usuário com passos manuais claros e específicos para o sistema operacional dele APENAS quando não houver ferramenta capaz de executar a ação automaticamente e se perceber que o usuário tem conhecimento necessário para isso.
- Mantenha o contexto da conversa. Lembre-se do que o usuário já disse nos turnos anteriores.
- Responda de forma profissional, prestativa e sempre em português.
- Não retorne códigos internos de chamadas de funções, tools e etc que e interno do sistema/chat/llm. Foque na experiência do usuário e na resolução do problema.";
    }

    private static string BuildSystemPrompt(Agent agent, AIIntegrationSettings aiSettings)
    {
        var configuredPrompt = aiSettings.PromptTemplate?.Trim();
        if (string.IsNullOrWhiteSpace(configuredPrompt))
            return BuildDefaultSystemPrompt(agent);

        return configuredPrompt
            .Replace("{{AgentId}}", agent.Id.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{Hostname}}", agent.Hostname ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{OperatingSystem}}", agent.OperatingSystem ?? "Desconhecido", StringComparison.OrdinalIgnoreCase)
            .Replace("{{OsVersion}}", agent.OsVersion ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{{SiteId}}", agent.SiteId.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{Status}}", agent.Status.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{LastIpAddress}}", agent.LastIpAddress ?? "Desconhecido", StringComparison.OrdinalIgnoreCase)
            .Replace("{{LastSeenAt}}", agent.LastSeenAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Nunca", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Versão assíncrona do BuildSystemPrompt com injeção de contexto RAG da KB.
    /// Retorna o prompt final e os IDs dos artigos injetados (para deduplicação em tool calls).
    /// </summary>
    private async Task<(string Prompt, List<Guid> InjectedArticleIds)> BuildSystemPromptAsync(
        Agent agent, AiChatSession session, string userMessage, AIIntegrationSettings aiSettings,
        Guid? departmentId, CancellationToken ct)
    {
        var basePrompt = BuildSystemPrompt(agent, aiSettings);

        // Injetar ferramentas do agente no prompt
        var agentTools = GetCachedAgentTools(agent.Id);
        if (agentTools is { Count: > 0 })
        {
            basePrompt = basePrompt.Replace("{{AGENT_TOOLS_SECTION}}",
                FormatAgentToolsDescription(agentTools));
        }
        else
        {
            basePrompt = basePrompt.Replace("{{AGENT_TOOLS_SECTION}}",
                "Nenhuma ferramenta do agente disponível. Oriente o usuário com passos manuais.");
        }
        var injected = new List<Guid>();

        if (!aiSettings.KnowledgeBaseEnabled || !aiSettings.EmbeddingEnabled || !aiSettings.EmbeddingArticlesEnabled)
            return (basePrompt, injected);

        try
        {
            // RAG: buscar chunks relevantes da KB no escopo do session
            var clientId = session.ClientId != Guid.Empty ? (Guid?)session.ClientId : null;
            var maxChunks = aiSettings.MaxKbChunks is >= 1 and <= 10 ? aiSettings.MaxKbChunks : 3;

            var embBaseUrl = string.IsNullOrWhiteSpace(aiSettings.EmbeddingBaseUrl) ? aiSettings.BaseUrl : aiSettings.EmbeddingBaseUrl;
            var embApiKey = string.IsNullOrWhiteSpace(aiSettings.EmbeddingApiKey) ? aiSettings.ApiKey : aiSettings.EmbeddingApiKey;
            var embedding = await _embeddingProvider.GenerateEmbeddingAsync(
                userMessage,
                aiSettings.EmbeddingModel,
                embApiKey,
                embBaseUrl,
                ct);
            var kbChunks = await _chunkRepository.SearchSemanticAsync(
                new Pgvector.Vector(embedding),
                clientId,
                session.SiteId,
                limit: maxChunks,
                minSimilarity: aiSettings.MinSimilarityScore,
                departmentId: departmentId,
                ct: ct);

            if (kbChunks.Count == 0)
                return (basePrompt, injected);

            var kbSection = new System.Text.StringBuilder();
            kbSection.AppendLine();
            kbSection.AppendLine();
            kbSection.AppendLine("## Base de Conhecimento (contexto relevante)");
            kbSection.AppendLine("Os seguintes artigos da base de conhecimento podem ser relevantes para a pergunta atual:");

            var totalTokens = 0;
            foreach (var chunk in kbChunks)
            {
                var chunkText = chunk.ChunkContent.Length > 800
                    ? chunk.ChunkContent[..800] + "..."
                    : chunk.ChunkContent;

                var estimatedTokens = (int)(chunkText.Split(' ').Length * 1.3);
                if (totalTokens + estimatedTokens > ClampKbContextTokens(aiSettings)) break;

                kbSection.AppendLine();
                var sectionLabel = string.IsNullOrEmpty(chunk.SectionTitle)
                    ? chunk.ArticleTitle
                    : $"{chunk.ArticleTitle} — {chunk.SectionTitle}";
                kbSection.AppendLine($"### {sectionLabel}");
                kbSection.AppendLine(chunkText);
                kbSection.AppendLine("---");
                totalTokens += estimatedTokens;
                injected.Add(chunk.ArticleId);
            }

            kbSection.AppendLine();
            kbSection.AppendLine("*Caso as informações acima não sejam suficientes, utilize a function call nativa `knowledge_search` para buscar mais artigos.*");

            return (basePrompt + kbSection.ToString(), injected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao injetar contexto RAG da KB. Continuando sem KB.");
            return (basePrompt, injected);
        }
    }
    
    /// <summary>
    /// Calcula o total de tokens usados na conversa via query eficiente.
    /// </summary>
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

        // Integra credencial por escopo (Site > Client > Global) — sobrescreve ApiKey e BaseUrl
        if (resolved.ClientId.HasValue)
        {
            var credential = await _credentialResolver.ResolveAsync(resolved.ClientId.Value, siteId, ct);
            if (credential is not null)
            {
                if (!string.IsNullOrWhiteSpace(credential.ApiKey))
                    ai.ApiKey = credential.ApiKey;
                if (!string.IsNullOrWhiteSpace(credential.BaseUrl))
                    ai.BaseUrl = credential.BaseUrl;
                if (!string.IsNullOrWhiteSpace(credential.EmbeddingBaseUrl))
                    ai.EmbeddingBaseUrl = credential.EmbeddingBaseUrl;
                if (!string.IsNullOrWhiteSpace(credential.EmbeddingApiKey))
                    ai.EmbeddingApiKey = credential.EmbeddingApiKey;
                if (!string.IsNullOrWhiteSpace(credential.Provider))
                    ai.Provider = credential.Provider;
            }
        }

        return ai;
    }

    private static int ClampHistoryMessages(AIIntegrationSettings settings)
        => settings.MaxHistoryMessages is >= 1 and <= 50 ? settings.MaxHistoryMessages : DefaultMaxHistoryMessages;

    private static int ClampKbContextTokens(AIIntegrationSettings settings)
        => settings.MaxKbContextTokens is >= 500 and <= 8000 ? settings.MaxKbContextTokens : DefaultMaxKbContextTokens;

    private static int ClampMaxTokens(AIIntegrationSettings settings)
        => settings.MaxTokensPerRequest is >= 100 and <= 8000 ? settings.MaxTokensPerRequest : DefaultMaxTokens;

    private static double ClampTemperature(AIIntegrationSettings settings)
        => settings.Temperature is >= 0 and <= 2 ? settings.Temperature : DefaultTemperature;

    /// <summary>
    /// Guardrails de saída: detecta e redige PII/secrets na resposta do LLM.
    /// </summary>
    private static string ApplyOutputGuardrails(string content, AIIntegrationSettings settings)
    {
        if (!settings.OutputGuardrailsEnabled || string.IsNullOrWhiteSpace(content))
            return content;

        var result = content;

        // Detectar API keys no formato comum (sk-..., key-..., etc.)
        result = Regex.Replace(result,
            @"\b(sk-[a-zA-Z0-9]{20,})\b",
            "***REDACTED_API_KEY***",
            RegexOptions.IgnoreCase);

        // Detectar tokens JWT
        result = Regex.Replace(result,
            @"\b(eyJ[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,})\b",
            "***REDACTED_JWT***");

        // Detectar senhas em padrão chave=valor
        result = Regex.Replace(result,
            @"(password|senha|passwd|secret|api[_-]?key)\s*[:=]\s*\S+",
            "$1: ***REDACTED***",
            RegexOptions.IgnoreCase);

        // Detectar CPF (formato brasileiro)
        result = Regex.Replace(result,
            @"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b",
            "***.###.###-**");

        return result;
    }

    /// <summary>
    /// Extrai a query de uma chamada knowledge_search para deduplicação.
    /// </summary>
    private static string ExtractKbQuery(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("query", out var qProp) && qProp.ValueKind == JsonValueKind.String)
                return qProp.GetString() ?? string.Empty;
        }
        catch { }
        return argumentsJson; // fallback: usa o JSON bruto como chave
    }

    /// <summary>
    /// Cache de respostas rápidas para mensagens muito curtas e comuns.
    /// Só aplicado quando não há histórico na sessão (primeira mensagem).
    /// </summary>
    private static readonly Dictionary<string, string> QuickReplies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["oi"] = "Olá! Como posso ajudar você hoje?",
        ["olá"] = "Olá! Em que posso ajudar?",
        ["ola"] = "Olá! Em que posso ajudar?",
        ["teste"] = "Olá! Como posso ajudar você hoje?",
        ["test"] = "Olá! Como posso ajudar você hoje?",
        ["bom dia"] = "Bom dia! Como posso ajudar você hoje?",
        ["boa tarde"] = "Boa tarde! Como posso ajudar você hoje?",
        ["boa noite"] = "Boa noite! Como posso ajudar você hoje?",
    };

    /// <summary>
    /// Tenta responder via cache rápido. Retorna true se respondeu.
    /// Só funciona para mensagens curtas, sem histórico, e em modo sync (não stream).
    /// </summary>
    internal static string? TryQuickReply(string message, IReadOnlyList<AiChatMessage>? history)
    {
        if (history is { Count: > 0 }) return null;
        var trimmed = message.Trim().ToLowerInvariant();
        if (QuickReplies.TryGetValue(trimmed, out var quick)) return quick;
        // Match parcial: "oi, tudo bem?" → "oi" (primeiras 3 palavras)
        if (trimmed.Length <= 20 && trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: <= 3 } words)
        {
            if (QuickReplies.TryGetValue(words[0], out var partial)) return partial;
        }
        return null;
    }

    private async Task PersistQuickReplyAsync(Guid sessionId, string userMessage, string reply,
        int seq, DateTime startTime, string traceId, AIIntegrationSettings aiSettings,
        Stopwatch stopwatch, CancellationToken ct)
    {
        try
        {
            var messages = new List<AiChatMessage>
            {
                new() { Id = Guid.NewGuid(), SessionId = sessionId, SequenceNumber = seq, Role = "user", Content = userMessage, CreatedAt = startTime, TraceId = traceId },
                new() { Id = Guid.NewGuid(), SessionId = sessionId, SequenceNumber = seq + 1, Role = "assistant", Content = reply, TokensUsed = reply.Split(' ').Length, LatencyMs = (int)stopwatch.ElapsedMilliseconds, ModelVersion = aiSettings.ChatModel + " (cached)", CreatedAt = DateTime.UtcNow, TraceId = traceId }
            };
            await _messageRepository.CreateBatchAsync(messages, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir quick-reply", traceId);
        }
    }

    /// <summary>
    /// Detecta tool calls em formato XML (ex: &lt;knowledgesearch&gt;{"query":"x"}&lt;/knowledge_search&gt;)
    /// geradas por modelos que não suportam function calling nativo (ex: Llama via certos providers).
    /// Executa as tools encontradas e retorna o texto limpo (sem os blocos XML).
    /// </summary>
    private async Task<(string Content, int NextSeq)> ParseAndExecuteXmlToolCallsAsync(
        string content,
        List<LlmTool> availableTools,
        Guid scopeClientId,
        Guid scopeSiteId,
        Guid agentId,
        AIIntegrationSettings aiSettings,
        Guid? departmentId,
        List<LlmMessage> llmMessages,
        List<AiChatMessage> toolMessagesToPersist,
        Guid sessionId,
        int nextSeq,
        string traceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (content, nextSeq);

        var matches = XmlToolCallRegex.Matches(content);
        if (matches.Count == 0)
            return (content, nextSeq);

        var knownToolNames = availableTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = content;
        var executedCount = 0;

        foreach (Match match in matches)
        {
            var rawToolName = match.Groups[1].Value;
            var argsJson = match.Groups[2].Value;

            // Resolve alias (ex: "knowledgesearch" → "knowledge_search")
            var toolName = XmlToolAliases.TryGetValue(rawToolName, out var resolved)
                ? resolved
                : rawToolName;

            if (!knownToolNames.Contains(toolName))
            {
                _logger.LogDebug("[{TraceId}] XML tool call ignorada (tool desconhecida): {ToolName}",
                    traceId, toolName);
                continue;
            }

            _logger.LogInformation("[{TraceId}] XML tool call detectada: {ToolName} args={Args}",
                traceId, toolName, argsJson);

            try
            {
                var toolResult = await _mcpToolExecutor.ExecuteAsync(
                    toolName, argsJson,
                    scopeClientId, scopeSiteId, agentId,
                    aiSettings, null, departmentId, sessionId, ct);

                var toolCallId = $"xml_{Guid.NewGuid():N}";

                llmMessages.Add(new LlmMessage("assistant", string.Empty));
                llmMessages.Add(new LlmMessage("tool", toolResult, toolCallId, toolName));

                toolMessagesToPersist.Add(new AiChatMessage
                {
                    Id = Guid.NewGuid(),
                    SessionId = sessionId,
                    SequenceNumber = nextSeq++,
                    Role = "tool",
                    Content = toolResult,
                    ToolCallId = toolCallId,
                    ToolName = toolName,
                    CreatedAt = DateTime.UtcNow,
                    TraceId = traceId
                });

                _logger.LogDebug("[{TraceId}] XML tool '{ToolName}' executada com sucesso",
                    traceId, toolName);
                executedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{TraceId}] XML tool '{ToolName}' falhou", traceId, toolName);
            }

            // Remove o bloco XML do texto
            result = result.Replace(match.Value, string.Empty);
        }

        if (executedCount > 0)
        {
            _logger.LogInformation("[{TraceId}] {Count} XML tool(s) executadas e removidas do output",
                traceId, executedCount);
        }

        return (result, nextSeq);
    }

    // ── Multi-Round Agent Tools ─────────────────────────────────────────────

    public async Task RegisterAgentToolsAsync(Guid agentId, Guid siteId,
        List<AgentToolRegistration> tools, CancellationToken ct = default)
    {
        var llmTools = tools.Select(t =>
        {
            object schema;
            try { schema = JsonSerializer.Deserialize<object>(t.ParametersSchemaJson)!; }
            catch { schema = new { type = "object", properties = new { } }; }
            return new LlmTool(t.Name, t.Description, schema);
        }).ToList();

        _agentToolsCache[agentId] = llmTools;
        _agentToolsCacheExpiry[agentId] = DateTime.UtcNow.Add(AgentToolsCacheTtl);
        _logger.LogInformation("[AgentTools] {Count} tools registradas para AgentId={AgentId}", llmTools.Count, agentId);
        await Task.CompletedTask;
    }

    private static List<LlmTool>? GetCachedAgentTools(Guid agentId)
    {
        if (_agentToolsCacheExpiry.TryGetValue(agentId, out var expiry) && expiry > DateTime.UtcNow)
            return _agentToolsCache.TryGetValue(agentId, out var tools) ? tools : null;
        return null;
    }

    /// <summary>
    /// Formata a descrição das tools do agente para inclusão no system prompt.
    /// Inclui nome e descrição resumida para orientar o LLM sobre capacidades disponíveis.
    /// </summary>
    private static string FormatAgentToolsDescription(List<LlmTool> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("O agente possui as seguintes ferramentas que podem ser usadas via function calling:");
        var hasCreateTicket = false;
        var hasSearchPackages = false;
        var hasAskUser = false;

        foreach (var tool in tools)
        {
            // Extrai primeira linha da descrição para manter conciso
            var desc = tool.Description;
            var firstLine = desc.Split('\n', '\r')[0];
            if (firstLine.Length > 120)
                firstLine = firstLine[..117] + "...";
            sb.AppendLine($"- `{tool.Name}`: {firstLine}");

            if (tool.Name == "create_ticket") hasCreateTicket = true;
            if (tool.Name == "search_packages") hasSearchPackages = true;
            if (tool.Name == "ask_user") hasAskUser = true;
        }
        sb.AppendLine();

        // Notas específicas para tools críticas que precisam de orientação extra
        if (hasSearchPackages)
            sb.AppendLine("⚠️ `search_packages`: o parâmetro `query` é OBRIGATÓRIO. SEMPRE extraia o nome do programa da mensagem do usuário. Se o usuário disser 'Foxit Reader', use query='Foxit Reader'.");
        if (hasAskUser)
            sb.AppendLine("⚠️ `ask_user`: o parâmetro `question` é OBRIGATÓRIO. SEMPRE preencha com uma pergunta clara baseada no contexto.");
        if (hasCreateTicket)
            sb.AppendLine("⚠️ `create_ticket`: use APÓS o usuário confirmar os dados do chamado. Preencha title, description, category e priority baseado no que foi discutido.");

        sb.AppendLine();
        sb.AppendLine("Use estas ferramentas quando o usuário solicitar ações relacionadas. Sempre preencha todos os parâmetros obrigatórios com os valores fornecidos pelo usuário.");
        return sb.ToString();
    }

    /// <summary>
    /// Detecta erros em respostas de tools do agente e os reformata com instruções
    /// para o LLM se autocorrigir. Se a resposta não parece um erro, retorna o original.
    /// </summary>
    private static string WrapAgentToolError(string rawResult, string toolName)
    {
        // Se já é JSON válido, retorna como está
        if (rawResult.TrimStart().StartsWith("{"))
            return rawResult;

        var lower = rawResult.ToLowerInvariant();

        // Detecta erros de parâmetro faltando/vazio
        if (lower.Contains("nao pode ser vazio") || lower.Contains("não pode ser vazio")
            || lower.Contains("cannot be empty") || lower.Contains("is required")
            || lower.Contains("é obrigatório") || lower.Contains("e obrigatorio")
            || lower.Contains("parameter") && lower.Contains("missing"))
        {
            // Hints específicos por tool para ajudar o LLM a se autocorrigir
            var hint = toolName switch
            {
                "search_packages" => "O parâmetro 'query' estava vazio. Extraia do histórico da conversa o nome do programa que o usuário quer pesquisar (ex: 'Foxit Reader', 'Chrome', '7-Zip'). Se o usuário disse 'Quero instalar o Foxit', use 'Foxit' como query.",
                "ask_user" => "O parâmetro 'question' estava vazio. Extraia da conversa o que você precisa perguntar ao usuário. Monte uma pergunta clara e específica baseada no contexto da conversa.",
                "create_ticket" => "Parâmetros obrigatórios do chamado estavam vazios. Extraia do histórico: título (resuma o problema), descrição (detalhes do que o usuário relatou), categoria (Software/Hardware/Rede/etc.) e prioridade (1-4).",
                "install_package" => "O parâmetro 'packageId' ou 'packageName' estava vazio. Extraia do histórico o nome/id do programa que o usuário pediu para instalar.",
                _ => "O LLM enviou parâmetros vazios ou ausentes. Extraia os valores corretos da conversa com o usuário e tente novamente preenchendo TODOS os parâmetros obrigatórios."
            };

            var json = JsonSerializer.Serialize(new
            {
                error = rawResult.Trim(),
                tool = toolName,
                hint
            });
            return json;
        }

        // Se o texto é curto e parece erro genérico (< 100 chars)
        if (rawResult.Length < 100 && (lower.Contains("erro") || lower.Contains("error")
            || lower.Contains("falha") || lower.Contains("fail")))
        {
            var json = JsonSerializer.Serialize(new
            {
                error = rawResult.Trim(),
                tool = toolName,
                hint = "A ferramenta retornou erro. Analise a mensagem de erro e corrija o problema antes de tentar novamente."
            });
            return json;
        }

        return rawResult;
    }

    public async IAsyncEnumerable<AiChatStreamChunk> StreamMultiRoundAsync(
        Guid agentId, string? message, Guid? sessionId,
        List<ToolResultItem>? toolResults, Guid? departmentId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var stopwatch = Stopwatch.StartNew();

        if (toolResults is not { Count: > 0 } && string.IsNullOrWhiteSpace(message))
        {
            yield return new AiChatStreamChunk(Type: "error", Error: "ToolResults ou Message requeridos.");
            yield break;
        }
        if (!sessionId.HasValue)
        {
            yield return new AiChatStreamChunk(Type: "error", Error: "SessionId requerido em multi-round.");
            yield break;
        }

        var session = await _sessionRepository.GetByIdAsync(sessionId.Value, agentId, ct);
        if (session == null)
        {
            yield return new AiChatStreamChunk(Type: "error", Error: $"Sessão {sessionId} não encontrada.");
            yield break;
        }

        var aiSettings = await ResolveAiSettingsAsync(session.SiteId, ct);
        if (!aiSettings.Enabled || !aiSettings.ChatAIEnabled)
        {
            yield return new AiChatStreamChunk(Type: "error", Error: "Chat IA desabilitado.");
            yield break;
        }

        var history = await _messageRepository.GetRecentBySessionAsync(session.Id, ClampHistoryMessages(aiSettings), ct);
        var nextSeq = history.Any() ? history.Max(m => m.SequenceNumber) + 1 : 1;

        var llmMessages = history.OrderBy(m => m.SequenceNumber)
            .Select(m => new LlmMessage(m.Role, m.Content, m.ToolCallId, m.ToolName)).ToList();

        // Persistir mensagem do usuário (quando houver) para manter contexto no banco
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
                _logger.LogDebug("[{TraceId}] StreamMultiRound: mensagem do usuário persistida (seq={Seq})", traceId, nextSeq - 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir mensagem do usuário no multi-round", traceId);
            }
        }

        if (toolResults is { Count: > 0 })
        {
            // Persistir tool results do agente no banco para manter o contexto entre rounds
            var toolMessagesToCreate = new List<AiChatMessage>();
            foreach (var tr in toolResults)
            {
                // Envolve erros em formato JSON com hint para o LLM se autocorrigir
                var wrappedResult = WrapAgentToolError(tr.Result, tr.Name);
                llmMessages.Add(new LlmMessage("tool", wrappedResult, $"agent_{tr.CallId}", tr.Name));
                toolMessagesToCreate.Add(new AiChatMessage
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    SequenceNumber = nextSeq++,
                    Role = "tool",
                    Content = wrappedResult,
                    ToolCallId = $"agent_{tr.CallId}",
                    ToolName = tr.Name,
                    CreatedAt = DateTime.UtcNow,
                    TraceId = traceId
                });
            }

            try
            {
                await _messageRepository.CreateBatchAsync(toolMessagesToCreate, ct);
                _logger.LogDebug("[{TraceId}] StreamMultiRound: {Count} tool results do agente persistidos",
                    traceId, toolMessagesToCreate.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{TraceId}] Falha ao persistir tool results do agente no multi-round", traceId);
            }
        }

        var agent = await _agentRepository.GetByIdAsync(agentId);
        if (agent == null) { yield return new AiChatStreamChunk(Type: "error", Error: "Agent não encontrado."); yield break; }

        var (systemPrompt, _) = await BuildSystemPromptAsync(agent, session,
            message ?? toolResults?.FirstOrDefault()?.Result ?? "", aiSettings, departmentId, ct);

        var availableTools = aiSettings.KnowledgeBaseEnabled
            ? await _mcpToolExecutor.GetAvailableToolsAsync(session.ClientId, session.SiteId, agentId, ct)
            : new List<LlmTool>();
        var agentTools = GetCachedAgentTools(agentId);
        if (agentTools is { Count: > 0 })
        {
            availableTools.AddRange(agentTools);
        }
        else
        {
            _logger.LogWarning("[AgentTools] Nenhuma tool de agente em cache para AgentId={AgentId} — " +
                               "o agent pode não ter registrado tools ou o cache expirou (TTL={Ttl})",
                agentId, AgentToolsCacheTtl);
        }

        var maxIterations = aiSettings.MaxToolCallIterations is >= 1 and <= 10
            ? aiSettings.MaxToolCallIterations : DefaultMaxToolCallIterations;

        var contentBuilder = new StringBuilder();
        var toolIterations = 0;
        int? totalTokens = null;
        var executedKbQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consecutiveEmptyKbSearches = 0;
        bool hasToolCalls = false;
        var agentToolCallNames = new HashSet<string>(agentTools?.Select(at => at.Name) ?? [], StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var streamOptions = new LlmOptions(
                ClampMaxTokens(aiSettings), ClampTemperature(aiSettings),
                string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                availableTools.Count > 0, availableTools,
                aiSettings.Provider, aiSettings.OpenRouterReferer, aiSettings.OpenRouterTitle, aiSettings.OpenRouterCategories,
                SessionId: session.Id.ToString("D"));

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
                    llmMessages.Add(new LlmMessage("assistant", contentBuilder.ToString()));

                    foreach (var tc in evt.ToolCalls)
                    {
                        if (agentToolCallNames.Contains(tc.Name))
                        {
                            hasAgentToolCall = true;
                            yield return new AiChatStreamChunk(Type: "tool_call",
                                ToolCallId: tc.Id, ToolName: tc.Name, ToolArgumentsDelta: tc.ArgumentsJson);
                        }
                        else
                        {
                            if (tc.Name == "knowledge_search")
                            {
                                var kbQuery = ExtractKbQuery(tc.ArgumentsJson);
                                if (!string.IsNullOrEmpty(kbQuery) && !executedKbQueries.Add(kbQuery)) continue;
                            }

                            var toolResult = await _mcpToolExecutor.ExecuteAsync(tc.Name, tc.ArgumentsJson,
                                session.ClientId, session.SiteId, agentId, aiSettings, null, departmentId, session.Id, ct);

                            yield return new AiChatStreamChunk(Type: "tool_result",
                                ToolCallId: tc.Id, ToolResult: toolResult);
                            llmMessages.Add(new LlmMessage("tool", toolResult, tc.Id, tc.Name));

                            if (tc.Name == "knowledge_search" && toolResult.Contains("\"found\":false"))
                                consecutiveEmptyKbSearches++;
                        }
                    }

                    if (hasAgentToolCall)
                    {
                        yield return new AiChatStreamChunk(Type: "round_end", SessionId: session.Id);
                        yield break;
                    }
                }
                else if (evt.Type == "done") { totalTokens = evt.TokensUsed; }
            }

            if (!hasToolCalls || toolIterations >= maxIterations - 1 || consecutiveEmptyKbSearches >= 2)
                break;
            toolIterations++;
        }

        stopwatch.Stop();
        var fullContent = contentBuilder.ToString();

        if (string.IsNullOrWhiteSpace(fullContent))
        {
            fullContent = "Não foi possível gerar uma resposta. Tente reformular sua pergunta.";
            yield return new AiChatStreamChunk(Type: "token", Content: fullContent);
        }

        try
        {
            await _messageRepository.CreateBatchAsync(new[]
            {
                new AiChatMessage
                {
                    Id = Guid.NewGuid(), SessionId = session.Id, SequenceNumber = nextSeq,
                    Role = "assistant", Content = fullContent, TokensUsed = totalTokens,
                    LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                    ModelVersion = aiSettings.ChatModel, CreatedAt = DateTime.UtcNow, TraceId = traceId
                }
            }, ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "[{TraceId}] Erro ao persistir multi-round", traceId); }

        yield return new AiChatStreamChunk(Type: "done", SessionId: session.Id,
            TokensUsed: totalTokens, LatencyMs: (int)stopwatch.ElapsedMilliseconds);
    }
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Serviço de chat IA integrado com agents do Meduza RMM
/// Orquestra chamadas OpenAI, gerencia histórico e processa tool calls MCP
/// </summary>
public class AiChatService : IAiChatService
{
    private readonly IAiChatSessionRepository _sessionRepository;
    private readonly IAiChatMessageRepository _messageRepository;
    private readonly IAiChatJobRepository _jobRepository;
    private readonly ILlmProvider _llmProvider;
    private readonly IAgentRepository _agentRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly ILoggingService _loggingService;
    private readonly ILogger<AiChatService> _logger;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IKnowledgeChunkRepository _chunkRepository;
    private readonly IKnowledgeMcpTool _knowledgeMcpTool;
    private readonly IConfigurationResolver _configurationResolver;
    
    private const int MaxMessageSizeBytes = 2048; // 2KB
    private const int SessionExpirationDays = 180;
    private const int MaxToolCallIterations = 3;
    private const int DefaultMaxHistoryMessages = 10;
    private const int DefaultMaxKbContextTokens = 2000;
    private const int DefaultMaxTokens = 1000;
    private const double DefaultTemperature = 0.7;
    
    public AiChatService(
        IAiChatSessionRepository sessionRepository,
        IAiChatMessageRepository messageRepository,
        IAiChatJobRepository jobRepository,
        ILlmProvider llmProvider,
        IAgentRepository agentRepository,
        ISiteRepository siteRepository,
        ILoggingService loggingService,
        ILogger<AiChatService> logger,
        IEmbeddingProvider embeddingProvider,
        IKnowledgeChunkRepository chunkRepository,
        IKnowledgeMcpTool knowledgeMcpTool,
        IConfigurationResolver configurationResolver)
    {
        _sessionRepository = sessionRepository;
        _messageRepository = messageRepository;
        _jobRepository = jobRepository;
        _llmProvider = llmProvider;
        _agentRepository = agentRepository;
        _siteRepository = siteRepository;
        _loggingService = loggingService;
        _logger = logger;
        _embeddingProvider = embeddingProvider;
        _chunkRepository = chunkRepository;
        _knowledgeMcpTool = knowledgeMcpTool;
        _configurationResolver = configurationResolver;
    }
    
    /// <summary>
    /// Processa uma mensagem de chat síncrona (rápida)
    /// </summary>
    public async Task<AgentChatSyncResponse> ProcessSyncAsync(
        Guid agentId, 
        string message, 
        Guid? sessionId, 
        CancellationToken ct)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var startTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            _logger.LogInformation(
                "[{TraceId}] ProcessSyncAsync iniciado para AgentId={AgentId}, SessionId={SessionId}",
                traceId, agentId, sessionId);
            
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
                    CreatedByIp = "0.0.0.0", // Será injetado pelo controller
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
            var (systemPrompt, injectedArticleIds) = await BuildSystemPromptAsync(agent, session, message, aiSettings, ct);
            
            // 7. Converter histórico para formato LLM
            var llmMessages = historyMessages
                .OrderBy(m => m.SequenceNumber)
                .Select(m => new LlmMessage(m.Role, m.Content, m.ToolCallId, m.ToolName))
                .ToList();
            
            // 8. Adicionar mensagem atual do usuário
            llmMessages.Add(new LlmMessage("user", message));
            
            // 9. Chamar LLM com tool call loop (MCP knowledge_search)
            var knowledgeSearchTool = new LlmTool(
                Name: "knowledge_search",
                Description: "Pesquisa artigos e procedimentos na base de conhecimento da empresa. Use quando o usuário perguntar sobre procedimentos, políticas, SOPs ou quando precisar de informações específicas documentadas.",
                Schema: new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "Termos de busca" },
                        max_results = new { type = "integer", description = "Número máximo de resultados (1-5)", @default = 3 }
                    },
                    required = new[] { "query" }
                });

            var llmOptions = new LlmOptions(
                MaxTokens: ClampMaxTokens(aiSettings),
                Temperature: ClampTemperature(aiSettings),
                Model: string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                BaseUrl: string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                ApiKey: string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                EnableTools: aiSettings.KnowledgeBaseEnabled,
                Tools: [knowledgeSearchTool]);
            
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
                    toolIterations >= MaxToolCallIterations)
                    break;

                toolIterations++;

                // Adiciona a resposta do assistant (com tool calls) ao contexto
                llmMessages.Add(new LlmMessage("assistant", llmResponse.Content ?? string.Empty));

                // Processa cada tool call
                foreach (var toolCall in llmResponse.ToolCalls)
                {
                    string toolResult;
                    if (toolCall.Name == "knowledge_search")
                    {
                        using var argsDoc = JsonDocument.Parse(toolCall.ArgumentsJson);
                        var query = argsDoc.RootElement.TryGetProperty("query", out var qProp)
                            ? qProp.GetString() ?? message
                            : message;
                        var maxRes = argsDoc.RootElement.TryGetProperty("max_results", out var mProp)
                            ? mProp.GetInt32()
                            : 3;

                        // Passa aiSettings já resolvidas (evita GetAISettingsAsync redundante) +
                        // IDs já injetados no system prompt (evita duplicar chunks)
                        toolResult = await _knowledgeMcpTool.ExecuteWithSettingsAsync(
                            scopeClientId,
                            scopeSiteId,
                            query,
                            aiSettings,
                            excludeArticleIds: injectedArticleIds,
                            maxRes,
                            ct);

                        _logger.LogDebug("[{TraceId}] MCP tool 'knowledge_search' executada ({Iter}/{Max})",
                            traceId, toolIterations, MaxToolCallIterations);
                    }
                    else
                    {
                        toolResult = $"{{\"error\": \"Tool '{toolCall.Name}' não reconhecida.\"}}"; 
                    }

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
            
            // 10. Persistir mensagem do usuário
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
            
            await _messageRepository.CreateAsync(userMessage, ct);
            
            // 11. Persistir mensagem do assistant
            var assistantMessage = new AiChatMessage
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                SequenceNumber = nextSequenceNumber + 1,
                Role = "assistant",
                Content = llmResponse.Content,
                TokensUsed = llmResponse.TokensUsed,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                ModelVersion = llmResponse.ModelVersion,
                CreatedAt = DateTime.UtcNow,
                TraceId = traceId
            };
            
            await _messageRepository.CreateAsync(assistantMessage, ct);
            
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
                AssistantMessage: llmResponse.Content,
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
        CancellationToken ct)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        
        try
        {
            _logger.LogInformation(
                "[{TraceId}] ProcessAsyncAsync iniciado para AgentId={AgentId}, SessionId={SessionId}",
                traceId, agentId, sessionId);
            
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
                    CreatedByIp = "0.0.0.0",
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
            
            // TODO: Enfileirar para processamento background (ReportGenerationBackgroundService ou similar)
            // Por enquanto, retorna JobId imediatamente
            
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
    /// Persiste as mensagens no DB ao final do stream.
    /// Não suporta tool calls — o contexto da KB é injetado no system prompt via RAG.
    /// </summary>
    public async IAsyncEnumerable<AiChatStreamChunk> StreamAsync(
        Guid agentId,
        string message,
        Guid? sessionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var startTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        AiChatSession? session = null;
        LlmOptions? llmOptions = null;
        string? systemPrompt = null;
        List<LlmMessage>? llmMessages = null;
        int nextSeq = 1;
        bool setupOk = false;
        string? setupError = null;

        try
        {
            ValidateUserInput(message);

            var agent = await _agentRepository.GetByIdAsync(agentId);
            if (agent == null)
                throw new ArgumentException($"Agent {agentId} não encontrado");

            var site = await _siteRepository.GetByIdAsync(agent.SiteId);
            if (site == null)
                throw new ArgumentException($"Site {agent.SiteId} não encontrado");

            var aiSettings = await ResolveAiSettingsAsync(agent.SiteId, ct);

            if (!aiSettings.Enabled || !aiSettings.ChatAIEnabled)
                throw new InvalidOperationException("Chat IA está desabilitado para este escopo.");

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
                    CreatedByIp = "0.0.0.0",
                    TraceId = traceId,
                    ExpiresAt = startTime.AddDays(SessionExpirationDays)
                }, ct);
            }

            var history = await _messageRepository.GetRecentBySessionAsync(
                session.Id, ClampHistoryMessages(aiSettings), ct);

            nextSeq = history.Any() ? history.Max(m => m.SequenceNumber) + 1 : 1;

            (systemPrompt, _) = await BuildSystemPromptAsync(agent, session, message, aiSettings, ct);

            llmMessages = history
                .OrderBy(m => m.SequenceNumber)
                .Select(m => new LlmMessage(m.Role, m.Content, m.ToolCallId, m.ToolName))
                .ToList();
            llmMessages.Add(new LlmMessage("user", message));

            llmOptions = new LlmOptions(
                MaxTokens: ClampMaxTokens(aiSettings),
                Temperature: ClampTemperature(aiSettings),
                Model: string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
                BaseUrl: string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
                ApiKey: string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
                EnableTools: false);

            setupOk = true;
        }
        catch (Exception ex)
        {
            setupError = ex.Message;
            _logger.LogError(ex, "[{TraceId}] StreamAsync setup falhou para AgentId={AgentId}", traceId, agentId);
        }

        if (!setupOk || session == null || llmOptions == null || systemPrompt == null || llmMessages == null)
        {
            yield return new AiChatStreamChunk(Type: "error", Error: setupError ?? "Erro interno");
            yield break;
        }

        // ── Streaming de tokens ───────────────────────────────────────────────
        var contentBuilder = new StringBuilder();

        await foreach (var token in _llmProvider.StreamAsync(systemPrompt, llmMessages, llmOptions, ct))
        {
            contentBuilder.Append(token);
            yield return new AiChatStreamChunk(Type: "token", Content: token);
        }

        stopwatch.Stop();
        var fullContent = contentBuilder.ToString();

        // ── Persistência pós-stream ───────────────────────────────────────────
        try
        {
            await _messageRepository.CreateAsync(new AiChatMessage
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                SequenceNumber = nextSeq,
                Role = "user",
                Content = message,
                CreatedAt = startTime,
                TraceId = traceId
            }, ct);

            await _messageRepository.CreateAsync(new AiChatMessage
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                SequenceNumber = nextSeq + 1,
                Role = "assistant",
                Content = fullContent,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                CreatedAt = DateTime.UtcNow,
                TraceId = traceId
            }, ct);

            _logger.LogInformation(
                "[{TraceId}] StreamAsync concluído: AgentId={AgentId}, Latency={LatencyMs}ms",
                traceId, agentId, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{TraceId}] Falha ao persistir mensagens do stream", traceId);
            // Não interrompe o cliente — stream já foi entregue
        }

        yield return new AiChatStreamChunk(
            Type: "done",
            SessionId: session.Id,
            LatencyMs: (int)stopwatch.ElapsedMilliseconds);
    }

    #region Private Methods
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
        return $@"Você é um assistente técnico especializado em suporte de TI e RMM (Remote Monitoring and Management).

**Contexto do Agent:**
- AgentId: {agent.Id}
- Hostname: {agent.Hostname}
- Sistema Operacional: {agent.OperatingSystem ?? "Desconhecido"} {agent.OsVersion ?? ""}
- Site: {agent.SiteId}
- Status: {agent.Status}
- Último IP: {agent.LastIpAddress ?? "Desconhecido"}
- Última comunicação: {agent.LastSeenAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Nunca"}

**Suas responsabilidades:**
1. Fornecer suporte técnico claro e direto
2. Diagnosticar problemas de forma sistemática
3. Sugerir soluções práticas e bem fundamentadas
4. Priorizar a segurança e estabilidade do sistema
5. Explicar conceitos técnicos de forma acessível

**Diretrizes:**
- Seja conciso e objetivo
- Use comandos específicos quando aplicável
- Identifique riscos e peça confirmação para operações críticas
- Se precisar executar ferramentas (comandos, scripts), informe claramente antes
- Mantenha o foco no problema relatado

Responda de forma profissional e prestativa.";
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
        Agent agent, AiChatSession session, string userMessage, AIIntegrationSettings aiSettings, CancellationToken ct)
    {
        var basePrompt = BuildSystemPrompt(agent, aiSettings);
        var injected = new List<Guid>();

        if (!aiSettings.KnowledgeBaseEnabled || !aiSettings.EmbeddingEnabled || !aiSettings.EmbeddingArticlesEnabled)
            return (basePrompt, injected);

        try
        {
            // RAG: buscar chunks relevantes da KB no escopo do session
            var clientId = session.ClientId != Guid.Empty ? (Guid?)session.ClientId : null;
            var maxChunks = aiSettings.MaxKbChunks is >= 1 and <= 10 ? aiSettings.MaxKbChunks : 3;

            var embedding = await _embeddingProvider.GenerateEmbeddingAsync(
                userMessage,
                aiSettings.EmbeddingModel,
                aiSettings.ApiKey,
                ct);
            var kbChunks = await _chunkRepository.SearchSemanticAsync(
                new Pgvector.Vector(embedding),
                clientId,
                session.SiteId,
                limit: maxChunks,
                minSimilarity: aiSettings.MinSimilarityScore,
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
            kbSection.AppendLine("*Use a tool `knowledge_search` se precisar buscar mais informações específicas na base de conhecimento.*");

            return (basePrompt + kbSection.ToString(), injected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao injetar contexto RAG da KB. Continuando sem KB.");
            return (basePrompt, injected);
        }
    }
    
    /// <summary>
    /// Calcula o total de tokens usados na conversa
    /// </summary>
    private async Task<int> CalculateConversationTokens(Guid sessionId, CancellationToken ct)
    {
        var allMessages = await _messageRepository.GetRecentBySessionAsync(
            sessionId, 
            int.MaxValue, 
            ct);
        
        return allMessages
            .Where(m => m.TokensUsed.HasValue)
            .Sum(m => m.TokensUsed!.Value);
    }

    private async Task<AIIntegrationSettings> ResolveAiSettingsAsync(Guid siteId, CancellationToken ct)
    {
        var resolved = await _configurationResolver.ResolveForSiteAsync(siteId);
        ct.ThrowIfCancellationRequested();
        return resolved.AIIntegration ?? new AIIntegrationSettings();
    }

    private static int ClampHistoryMessages(AIIntegrationSettings settings)
        => settings.MaxHistoryMessages is >= 1 and <= 50 ? settings.MaxHistoryMessages : DefaultMaxHistoryMessages;

    private static int ClampKbContextTokens(AIIntegrationSettings settings)
        => settings.MaxKbContextTokens is >= 500 and <= 8000 ? settings.MaxKbContextTokens : DefaultMaxKbContextTokens;

    private static int ClampMaxTokens(AIIntegrationSettings settings)
        => settings.MaxTokensPerRequest is >= 100 and <= 8000 ? settings.MaxTokensPerRequest : DefaultMaxTokens;

    private static double ClampTemperature(AIIntegrationSettings settings)
        => settings.Temperature is >= 0 and <= 2 ? settings.Temperature : DefaultTemperature;
    
    #endregion
}

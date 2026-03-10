using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
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
    private readonly ILoggingService _loggingService;
    private readonly ILogger<AiChatService> _logger;
    
    private const int MaxMessageSizeBytes = 2048; // 2KB
    private const int MaxHistoryMessages = 10;
    private const int SessionExpirationDays = 180;
    
    public AiChatService(
        IAiChatSessionRepository sessionRepository,
        IAiChatMessageRepository messageRepository,
        IAiChatJobRepository jobRepository,
        ILlmProvider llmProvider,
        IAgentRepository agentRepository,
        ILoggingService loggingService,
        ILogger<AiChatService> logger)
    {
        _sessionRepository = sessionRepository;
        _messageRepository = messageRepository;
        _jobRepository = jobRepository;
        _llmProvider = llmProvider;
        _agentRepository = agentRepository;
        _loggingService = loggingService;
        _logger = logger;
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
                    ClientId = Guid.Empty, // Será preenchido pelo controller com contexto do Site
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
                MaxHistoryMessages, 
                ct);
            
            // 5. Determinar próximo SequenceNumber
            var nextSequenceNumber = historyMessages.Any() 
                ? historyMessages.Max(m => m.SequenceNumber) + 1 
                : 1;
            
            // 6. Build system prompt com contexto do agent
            var systemPrompt = BuildSystemPrompt(agent);
            
            // 7. Converter histórico para formato LLM
            var llmMessages = historyMessages
                .OrderBy(m => m.SequenceNumber)
                .Select(m => new LlmMessage(m.Role, m.Content, m.ToolCallId, m.ToolName))
                .ToList();
            
            // 8. Adicionar mensagem atual do usuário
            llmMessages.Add(new LlmMessage("user", message));
            
            // 9. Chamar LLM (OpenAI)
            var llmOptions = new LlmOptions(
                MaxTokens: 1000,
                Temperature: 0.7,
                EnableTools: false, // TODO: Fase 2 - MCP tools
                Tools: null
            );
            
            var llmResponse = await _llmProvider.CompleteAsync(
                systemPrompt,
                llmMessages,
                llmOptions,
                ct);
            
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
                clientId: session.ClientId != Guid.Empty ? session.ClientId.ToString() : null,
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
                    ClientId = Guid.Empty,
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
    
    #region Private Methods
    
    /// <summary>
    /// Valida a mensagem do usuário
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
    private string BuildSystemPrompt(Agent agent)
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
    
    #endregion
}

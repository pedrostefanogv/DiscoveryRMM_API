# Plano de Implementação: Integração de Chat IA com Agents

**Versão:** 1.0  
**Data:** 9 de março de 2026  
**Status:** Planejamento  

## Índice

1. [Visão Geral](#visão-geral)
2. [Arquitetura de Segurança](#arquitetura-de-segurança)
3. [Escopo e Decisões](#escopo-e-decisões)
4. [Fases de Implementação](#fases-de-implementação)
5. [Integração MCP (Model Context Protocol)](#integração-mcp)
6. [Modelo de Dados](#modelo-de-dados)
7. [Endpoints da API](#endpoints-da-api)
8. [Segurança e Controles](#segurança-e-controles)
9. [Observabilidade e Auditoria](#observabilidade-e-auditoria)
10. [Verificação e Testes](#verificação-e-testes)
11. [Riscos e Mitigações](#riscos-e-mitigações)
12. [Considerações Futuras](#considerações-futuras)

---

## Visão Geral

### Objetivo
Implementar capacidade de chat IA para agents do Meduza RMM, permitindo:
- Suporte técnico interativo via IA (OpenAI ChatGPT)
- Execução de ferramentas locais via MCP (Model Context Protocol)
- Histórico completo de conversas com auditoria
- **Zero exposição** de segredos (API keys) no agent

### Princípios de Arquitetura
1. **Segredo centralizado**: API key OpenAI apenas no servidor
2. **Agent como executor**: MCP local para tools, sem acesso direto à IA
3. **Servidor como orquestrador**: API Meduza coordena conversa + tools
4. **Isolamento por AgentId**: Histórico e políticas isoladas por agent
5. **Auditoria completa**: TraceId em todo o fluxo, logs sanitizados

---

## Arquitetura de Segurança

### Fluxo de Comunicação

```
┌──────────────────────────────────────────────────────────────────┐
│ AGENT (Windows/Linux)                                            │
│ ├─ MCP Server Local (stdio/localhost)                            │
│ │  └─ Tools: FileSystem, Registry, WMI, PowerShell (allowlist)   │
│ └─ HTTP Client (autenticado via AgentToken)                      │
└────────────────┬─────────────────────────────────────────────────┘
                 │
                 │ HTTPS + Bearer Token (mdz_xxxxx)
                 ▼
┌──────────────────────────────────────────────────────────────────┐
│ MEDUZA API SERVER                                                │
│ ├─ AgentAuthMiddleware (valida token, extrai AgentId)            │
│ ├─ AiChatController                                              │
│ │  ├─ POST /api/agent-auth/me/ai-chat (sync curta)              │
│ │  ├─ POST /api/agent-auth/me/ai-chat/async (longa)             │
│ │  └─ GET  /api/agent-auth/me/ai-chat/jobs/{id}                 │
│ └─ AiChatService (orquestrador)                                  │
│    ├─ Fetch histórico (SQLdb)                                    │
│    ├─ Build context + system prompt                              │
│    ├─ Call OpenAI (API key env var)                              │
│    ├─ Detect tool_calls                                          │
│    └─ Persist mensagens + audit                                  │
└────────────────┬─────────────────────────────────────────────────┘
                 │
                 │ SE TOOL_CALL DETECTADA
                 ▼
┌──────────────────────────────────────────────────────────────────┐
│ AGENT COMMAND FLOW                                               │
│ ├─ API cria AgentCommand (type: ExecuteMcpTool, payload signed)  │
│ ├─ Agent recebe via polling ou SignalR                           │
│ ├─ Agent valida signature + allowlist                            │
│ ├─ Agent chama MCP local: tool(args) → result                    │
│ ├─ Agent envia CommandResult (sanitizado, max 10KB)              │
│ └─ API recebe resultado → re-call OpenAI com tool output         │
└──────────────────────────────────────────────────────────────────┘
```

### Princípios de Segurança

| Camada | Controle | Implementação |
|--------|----------|---------------|
| **Transport** | TLS obrigatório | HTTPS em produção, reject HTTP |
| **Autenticação** | Token hash SHA-256 | AgentAuthMiddleware + AgentTokenRepository |
| **Autorização** | Isolamento por AgentId | Policy: agent X só acessa conversas X |
| **Segredo** | Env var servidor | `OPENAI_API_KEY` nunca em DB/logs/response |
| **Tool Allowlist** | Por AgentId/SiteId/ClientId | Tabela `mcp_tool_policies` com schema validation |
| **Rate Limit** | Por AgentId | 10 req/min chat, 5 tool calls/min |
| **Sanitização** | LoggingService | Redact: password, token, secret, api_key, bearer |
| **Timeout** | OpenAI: 30s, MCP: 10s | CancellationToken em todos os calls |
| **Audit Trail** | TraceId + timestamps | LogEntry + AiChatMessage com full context |

---

## Escopo e Decisões

### ✅ Incluído (MVP)
- Chat iniciado apenas por agent (não dashboard humano)
- Respostas híbridas: síncrona curta (<5s) + assíncrona longa (job)
- Histórico de conversas com retenção de **180 dias**
- Isolamento principal por **AgentId**
- Segredo OpenAI em **variável de ambiente do servidor**
- Execução de tools MCP locais no agent (allowlist estrita)
- Logs com TraceId, sanitização automática
- Rate limiting por AgentId
- Purge automático de histórico expirado

### ❌ Excluído desta fase
- Interface de dashboard para chat humano-IA
- Multitenancy por ClientId como chave primária de conversa
- Troca de provider por UI (OpenAI hardcoded primeira fase)
- Anexos/uploads de arquivos no chat
- Fine-tuning de modelo customizado
- Streaming de respostas via SSE (apenas polling/async)
- Integração com ticketing automático

---

## Fases de Implementação

### **Fase 1: Contratos e Modelo de Dados** *(Bloqueante para todas as fases)*

#### 1.1 DTOs de Chat na API
**Arquivo:** `src/Meduza.Core/DTOs/AiChatDtos.cs`

```csharp
// Request do agent para chat síncrono
public record AgentChatRequest(
    string Message,
    Guid? SessionId = null, // null = nova sessão
    int? MaxTokens = 1000
);

// Resposta síncrona (curta)
public record AgentChatSyncResponse(
    Guid SessionId,
    string AssistantMessage,
    int TokensUsed,
    int ConversationTokensTotal,
    int LatencyMs
);

// Request assíncrono (longa)
public record AgentChatAsyncRequest(
    string Message,
    Guid? SessionId = null,
    int? MaxTokens = 2000
);

// Status do job assíncrono
public record AgentChatJobStatus(
    Guid JobId,
    string Status, // Pending, Processing, Completed, Failed, Timeout
    Guid SessionId,
    string? AssistantMessage,
    int? TokensUsed,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? CompletedAt
);
```

#### 1.2 Entidades de Histórico
**Arquivo:** `src/Meduza.Core/Entities/AiChatSession.cs`

```csharp
public class AiChatSession
{
    public Guid Id { get; set; }
    
    // Contexto
    public Guid AgentId { get; set; }
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }
    
    // Metadata
    public string? Topic { get; set; } // "troubleshooting", "advisory", etc
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    
    // Audit
    public string CreatedByIp { get; set; } = string.Empty;
    public string? TraceId { get; set; }
    
    // Retenção
    public DateTime ExpiresAt { get; set; } // CreatedAt + 180 dias
    public DateTime? DeletedAt { get; set; } // Soft delete
    
    // Relacionamentos
    public Agent Agent { get; set; } = null!;
    public ICollection<AiChatMessage> Messages { get; set; } = [];
}
```

**Arquivo:** `src/Meduza.Core/Entities/AiChatMessage.cs`

```csharp
public class AiChatMessage
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    
    public int SequenceNumber { get; set; } // 1, 2, 3...
    public string Role { get; set; } = string.Empty; // "user", "assistant", "system", "tool"
    public string Content { get; set; } = string.Empty;
    
    // Metadata
    public int? TokensUsed { get; set; }
    public int? LatencyMs { get; set; }
    public string? ModelVersion { get; set; } // "gpt-4-turbo", etc
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Tool execution (se role = "tool")
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolArgumentsJson { get; set; }
    
    // Audit
    public string? TraceId { get; set; }
    
    // Relacionamentos
    public AiChatSession Session { get; set; } = null!;
}
```

**Arquivo:** `src/Meduza.Core/Entities/AiChatJob.cs` (para requests assíncronas)

```csharp
public class AiChatJob
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid AgentId { get; set; }
    
    public string Status { get; set; } = "Pending"; // Pending, Processing, Completed, Failed, Timeout
    public string UserMessage { get; set; } = string.Empty;
    public string? AssistantMessage { get; set; }
    public int? TokensUsed { get; set; }
    public string? ErrorMessage { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    
    public string? TraceId { get; set; }
}
```

#### 1.3 Migration
**Arquivo:** `src/Meduza.Migrations/Migrations/M045_CreateAiChatTables.cs`

```csharp
[Migration(45)]
public class M045_CreateAiChatTables : Migration
{
    public override void Up()
    {
        // ai_chat_sessions
        Create.Table("ai_chat_sessions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("agents", "id")
            .WithColumn("site_id").AsGuid().NotNullable().ForeignKey("sites", "id")
            .WithColumn("client_id").AsGuid().NotNullable().ForeignKey("clients", "id")
            .WithColumn("topic").AsString(100).Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("closed_at").AsDateTime().Nullable()
            .WithColumn("created_by_ip").AsString(45).NotNullable()
            .WithColumn("trace_id").AsString(100).Nullable()
            .WithColumn("expires_at").AsDateTime().NotNullable()
            .WithColumn("deleted_at").AsDateTime().Nullable();
            
        Create.Index("ix_ai_chat_sessions_agent_created")
            .OnTable("ai_chat_sessions")
            .OnColumn("agent_id").Ascending()
            .OnColumn("created_at").Descending();
            
        Create.Index("ix_ai_chat_sessions_expires")
            .OnTable("ai_chat_sessions")
            .OnColumn("expires_at").Ascending();

        // ai_chat_messages
        Create.Table("ai_chat_messages")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("session_id").AsGuid().NotNullable().ForeignKey("ai_chat_sessions", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("sequence_number").AsInt32().NotNullable()
            .WithColumn("role").AsString(20).NotNullable()
            .WithColumn("content").AsString(int.MaxValue).NotNullable()
            .WithColumn("tokens_used").AsInt32().Nullable()
            .WithColumn("latency_ms").AsInt32().Nullable()
            .WithColumn("model_version").AsString(50).Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("tool_name").AsString(100).Nullable()
            .WithColumn("tool_call_id").AsString(100).Nullable()
            .WithColumn("tool_arguments_json").AsString(int.MaxValue).Nullable()
            .WithColumn("trace_id").AsString(100).Nullable();
            
        Create.Index("ix_ai_chat_messages_session_sequence")
            .OnTable("ai_chat_messages")
            .OnColumn("session_id").Ascending()
            .OnColumn("sequence_number").Ascending();

        // ai_chat_jobs
        Create.Table("ai_chat_jobs")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("session_id").AsGuid().NotNullable().ForeignKey("ai_chat_sessions", "id")
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("agents", "id")
            .WithColumn("status").AsString(20).NotNullable()
            .WithColumn("user_message").AsString(int.MaxValue).NotNullable()
            .WithColumn("assistant_message").AsString(int.MaxValue).Nullable()
            .WithColumn("tokens_used").AsInt32().Nullable()
            .WithColumn("error_message").AsString(1000).Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("started_at").AsDateTime().Nullable()
            .WithColumn("completed_at").AsDateTime().Nullable()
            .WithColumn("trace_id").AsString(100).Nullable();
            
        Create.Index("ix_ai_chat_jobs_agent_status")
            .OnTable("ai_chat_jobs")
            .OnColumn("agent_id").Ascending()
            .OnColumn("status").Ascending()
            .OnColumn("created_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("ai_chat_jobs");
        Delete.Table("ai_chat_messages");
        Delete.Table("ai_chat_sessions");
    }
}
```

---

### **Fase 2: Camada de Provider e Segurança de Segredo** *(Depende de Fase 1)*

#### 2.1 Interface de Provider LLM
**Arquivo:** `src/Meduza.Core/Interfaces/ILlmProvider.cs`

```csharp
public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        LlmOptions options,
        CancellationToken cancellationToken = default);
}

public record LlmMessage(string Role, string Content, string? ToolCallId = null, string? ToolName = null);

public record LlmOptions(
    int MaxTokens = 1000,
    double Temperature = 0.7,
    bool EnableTools = false,
    List<LlmTool>? Tools = null);

public record LlmTool(string Name, string Description, object Schema);

public record LlmResponse(
    string Content,
    int TokensUsed,
    string ModelVersion,
    List<LlmToolCall>? ToolCalls = null);

public record LlmToolCall(string Id, string Name, string ArgumentsJson);
```

#### 2.2 Implementação OpenAI
**Arquivo:** `src/Meduza.Infrastructure/Services/OpenAiProvider.cs`

```csharp
public class OpenAiProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<OpenAiProvider> _logger;

    public OpenAiProvider(IConfiguration config, ILogger<OpenAiProvider> logger)
    {
        _apiKey = config.GetValue<string>("OpenAI:ApiKey") 
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured in environment");
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/v1/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        _logger = logger;
    }

    public async Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        // Implementação usando OpenAI Chat Completions API
        // POST /chat/completions
        // { model: "gpt-4-turbo", messages: [...], max_tokens: ..., tools: [...] }
        
        // IMPORTANTE: NUNCA logar _apiKey
        _logger.LogInformation("Calling OpenAI with {MessageCount} messages, maxTokens={MaxTokens}", 
            messages.Count, options.MaxTokens);
            
        // ... implementação real ...
    }
}
```

#### 2.3 Configuração e Startup
**Arquivo:** `src/Meduza.Api/Program.cs` (adicionar)

```csharp
// AI Provider
var openAiKey = builder.Configuration.GetValue<string>("OpenAI:ApiKey");
if (string.IsNullOrEmpty(openAiKey) && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "CRITICAL: OpenAI:ApiKey must be set in production environment (env var OPENAI__APIKEY)");
}

builder.Services.AddSingleton<ILlmProvider, OpenAiProvider>();
builder.Services.AddScoped<IAiChatService, AiChatService>();
builder.Services.AddScoped<IAiChatSessionRepository, AiChatSessionRepository>();
builder.Services.AddScoped<IAiChatMessageRepository, AiChatMessageRepository>();
builder.Services.AddScoped<IAiChatJobRepository, AiChatJobRepository>();
```

**Arquivo:** `appsettings.json` (adicionar)

```json
{
  "OpenAI": {
    "ApiKey": "",  // NUNCA commitar chave real; usar env var
    "Model": "gpt-4-turbo",
    "MaxTokensDefault": 1000,
    "TimeoutSeconds": 30
  }
}
```

**Variável de ambiente (produção):**
```bash
export OPENAI__APIKEY="sk-proj-xxxxxxxxxxxxxxxxxxxxx"
```

#### 2.4 Sanitização Reforçada
**Arquivo:** `src/Meduza.Infrastructure/Services/LoggingService.cs` (atualizar)

```csharp
private static string SanitizeMessage(string message)
{
    var patterns = new[]
    {
        "password", "passwd", "pwd", "token", "auth", "secret", 
        "key", "api_key", "apikey", "authorization", "bearer",
        "sk-proj-", "sk-", "openai"  // <-- ADICIONAR padrões OpenAI
    };
    
    foreach (var pattern in patterns)
    {
        var regex = new Regex($@"{pattern}[:\s=]+[^\s&]+", RegexOptions.IgnoreCase);
        message = regex.Replace(message, $"{pattern}=[REDACTED]");
    }
    
    return message;
}
```

---

### **Fase 3: Orquestração do Chat para Agent** *(Depende de Fase 2)*

#### 3.1 Serviço de Chat
**Arquivo:** `src/Meduza.Infrastructure/Services/AiChatService.cs`

```csharp
public interface IAiChatService
{
    Task<AgentChatSyncResponse> ProcessSyncAsync(
        Guid agentId, string message, Guid? sessionId, CancellationToken ct);
        
    Task<Guid> ProcessAsyncAsync(
        Guid agentId, string message, Guid? sessionId, CancellationToken ct);
        
    Task<AgentChatJobStatus> GetJobStatusAsync(Guid jobId, Guid agentId, CancellationToken ct);
}

public class AiChatService : IAiChatService
{
    private readonly IAiChatSessionRepository _sessionRepo;
    private readonly IAiChatMessageRepository _messageRepo;
    private readonly IAiChatJobRepository _jobRepo;
    private readonly ILlmProvider _llmProvider;
    private readonly IAgentRepository _agentRepo;
    private readonly ILoggingService _loggingService;
    private readonly IConfiguration _config;
    
    // Threshold: sync se estimativa < 5s, senão async
    private const int SyncThresholdTokens = 500;

    public async Task<AgentChatSyncResponse> ProcessSyncAsync(
        Guid agentId, string message, Guid? sessionId, CancellationToken ct)
    {
        var traceId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        var startTime = DateTime.UtcNow;
        
        // 1. Validar input (max 2KB, sem scripts)
        ValidateUserInput(message);
        
        // 2. Obter ou criar sessão
        var session = sessionId.HasValue
            ? await _sessionRepo.GetByIdAsync(sessionId.Value, agentId, ct)
            : await CreateNewSessionAsync(agentId, traceId, ct);
            
        if (session == null || session.AgentId != agentId)
            throw new UnauthorizedAccessException("Session not found or unauthorized");
        
        // 3. Buscar histórico recente (últimas 10 mensagens para contexto)
        var recentMessages = await _messageRepo.GetRecentBySessionAsync(session.Id, 10, ct);
        
        // 4. Build system prompt com contexto do agent
        var agent = await _agentRepo.GetByIdAsync(agentId, ct);
        var systemPrompt = BuildSystemPrompt(agent);
        
        // 5. Preparar mensagens para LLM
        var llmMessages = recentMessages
            .Select(m => new LlmMessage(m.Role, m.Content, m.ToolCallId, m.ToolName))
            .Append(new LlmMessage("user", message))
            .ToList();
        
        // 6. Call OpenAI
        var response = await _llmProvider.CompleteAsync(
            systemPrompt,
            llmMessages,
            new LlmOptions(MaxTokens: 1000, EnableTools: false),
            ct);
        
        // 7. Persistir mensagens (user + assistant)
        var nextSeq = recentMessages.Any() ? recentMessages.Max(m => m.SequenceNumber) + 1 : 1;
        
        await _messageRepo.CreateAsync(new AiChatMessage
        {
            SessionId = session.Id,
            SequenceNumber = nextSeq,
            Role = "user",
            Content = message,
            CreatedAt = startTime,
            TraceId = traceId
        }, ct);
        
        await _messageRepo.CreateAsync(new AiChatMessage
        {
            SessionId = session.Id,
            SequenceNumber = nextSeq + 1,
            Role = "assistant",
            Content = response.Content,
            TokensUsed = response.TokensUsed,
            LatencyMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds,
            ModelVersion = response.ModelVersion,
            CreatedAt = DateTime.UtcNow,
            TraceId = traceId
        }, ct);
        
        // 8. Auditoria
        await _loggingService.LogAsync(new LogEntry
        {
            ClientId = session.ClientId,
            SiteId = session.SiteId,
            AgentId = agentId,
            Type = "AiChat",
            Level = "Info",
            Source = "AiChatService",
            Message = "Chat sync completed",
            DataJson = JsonSerializer.Serialize(new { sessionId, tokensUsed = response.TokensUsed })
        });
        
        return new AgentChatSyncResponse(
            session.Id,
            response.Content,
            response.TokensUsed,
            recentMessages.Sum(m => m.TokensUsed ?? 0) + response.TokensUsed,
            (int)(DateTime.UtcNow - startTime).TotalMilliseconds);
    }

    // ProcessAsyncAsync: cria job, processa em background, retorna jobId
    // GetJobStatusAsync: consulta status + resultado do job
}
```

#### 3.2 Controller
**Arquivo:** `src/Meduza.Api/Controllers/AgentAuthController.cs` (adicionar)

```csharp
[HttpPost("me/ai-chat")]
public async Task<IActionResult> ChatSync([FromBody] AgentChatRequest request, CancellationToken ct)
{
    if (!TryGetAuthenticatedAgentId(out var agentId))
        return Unauthorized();
    
    try
    {
        var response = await _aiChatService.ProcessSyncAsync(agentId, request.Message, request.SessionId, ct);
        return Ok(response);
    }
    catch (OperationCanceledException)
    {
        return StatusCode(408, new { error = "Request timeout" });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Chat sync failed for agent {AgentId}", agentId);
        return StatusCode(500, new { error = "Internal error" });
    }
}

[HttpPost("me/ai-chat/async")]
public async Task<IActionResult> ChatAsync([FromBody] AgentChatAsyncRequest request, CancellationToken ct)
{
    if (!TryGetAuthenticatedAgentId(out var agentId))
        return Unauthorized();
    
    var jobId = await _aiChatService.ProcessAsyncAsync(agentId, request.Message, request.SessionId, ct);
    return Accepted(new { jobId, statusUrl = $"/api/agent-auth/me/ai-chat/jobs/{jobId}" });
}

[HttpGet("me/ai-chat/jobs/{jobId}")]
public async Task<IActionResult> GetJobStatus(Guid jobId, CancellationToken ct)
{
    if (!TryGetAuthenticatedAgentId(out var agentId))
        return Unauthorized();
    
    var status = await _aiChatService.GetJobStatusAsync(jobId, agentId, ct);
    return Ok(status);
}
```

---

### **Fase 4: Histórico, Auditoria e Retenção** *(Depende de Fase 3)*

#### 4.1 Repositórios
**Arquivo:** `src/Meduza.Infrastructure/Repositories/AiChatSessionRepository.cs`

```csharp
public interface IAiChatSessionRepository
{
    Task<AiChatSession> CreateAsync(AiChatSession session, CancellationToken ct);
    Task<AiChatSession?> GetByIdAsync(Guid id, Guid agentId, CancellationToken ct);
    Task<List<AiChatSession>> GetByAgentAsync(Guid agentId, int limit, CancellationToken ct);
    Task<List<AiChatSession>> GetExpiredAsync(DateTime cutoff, int limit, CancellationToken ct);
    Task<int> SoftDeleteAsync(Guid id, CancellationToken ct);
    Task<int> HardDeleteAsync(DateTime deletedBefore, CancellationToken ct);
}
```

#### 4.2 Background Service de Retenção
**Arquivo:** `src/Meduza.Api/Services/AiChatRetentionBackgroundService.cs`

```csharp
public class AiChatRetentionBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiChatRetentionBackgroundService> _logger;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessRetentionAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI chat retention job failed");
            }
        }
    }
    
    private async Task ProcessRetentionAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sessionRepo = scope.ServiceProvider.GetRequiredService<IAiChatSessionRepository>();
        var loggingService = scope.ServiceProvider.GetRequiredService<ILoggingService>();
        
        var cutoff = DateTime.UtcNow.AddDays(-180); // 180 dias
        
        // 1. Soft delete sessões expiradas
        var expiredSessions = await sessionRepo.GetExpiredAsync(cutoff, 1000, ct);
        foreach (var session in expiredSessions)
        {
            await sessionRepo.SoftDeleteAsync(session.Id, ct);
        }
        
        // 2. Hard delete após 30 dias de grace period (LGPD)
        var hardDeleteCutoff = DateTime.UtcNow.AddDays(-210); // 180 + 30
        var deletedCount = await sessionRepo.HardDeleteAsync(hardDeleteCutoff, ct);
        
        _logger.LogInformation(
            "AI chat retention: soft-deleted {SoftCount}, hard-deleted {HardCount}",
            expiredSessions.Count, deletedCount);
            
        await loggingService.LogAsync(new LogEntry
        {
            Type = "System",
            Level = "Info",
            Source = "AiChatRetention",
            Message = $"Retention complete: {expiredSessions.Count} expired, {deletedCount} purged"
        });
    }
}
```

**Registrar em Program.cs:**
```csharp
builder.Services.AddHostedService<AiChatRetentionBackgroundService>();
```

---

## Integração MCP (Model Context Protocol)

### Visão Geral MCP

O Model Context Protocol permite que a IA execute ferramentas locais no agent sem expor segredos. O servidor MCP roda **no agent**, expondo tools via stdio ou HTTP localhost.

### Arquitetura MCP

```
┌─────────────────────────────────────────────────────────┐
│ MEDUZA API (Orquestrador)                               │
│ ├─ OpenAI detecta tool_use necessária                   │
│ ├─ Cria AgentCommand(type: ExecuteMcpTool)              │
│ ├─ Payload assinado: { tool, args, nonce, signature }   │
│ └─ Aguarda CommandResult                                │
└────────────────┬────────────────────────────────────────┘
                 │
                 │ SignalR ou Polling
                 ▼
┌─────────────────────────────────────────────────────────┐
│ AGENT (Client)                                          │
│ ├─ Recebe comando ExecuteMcpTool                        │
│ ├─ Valida signature (HMAC-SHA256 com AgentToken)        │
│ ├─ Verifica allowlist: tool permitida?                  │
│ ├─ Timeout: 10s máximo                                  │
│ └─ Conecta ao MCP Server Local                          │
│                                                          │
│    ┌────────────────────────────────────────┐           │
│    │ MCP SERVER (stdio ou localhost:3000)   │           │
│    │ ├─ filesystem.read_file                │           │
│    │ ├─ registry.get_value (Windows)        │           │
│    │ ├─ wmi.query (Windows)                 │           │
│    │ ├─ systemctl.status (Linux)            │           │
│    │ └─ custom.diagnostic_tool              │           │
│    └────────────────────────────────────────┘           │
│                                                          │
│ ├─ Executa: mcp_client.call_tool(tool, args)            │
│ ├─ Sanitiza resultado (remove paths sensíveis)          │
│ ├─ Limita output (max 10KB)                             │
│ └─ Envia CommandResult(exitCode, output)                │
└─────────────────┬────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────┐
│ MEDUZA API                                              │
│ ├─ Recebe tool output                                   │
│ ├─ Re-call OpenAI com tool result                       │
│ └─ Retorna resposta final ao agent                      │
└─────────────────────────────────────────────────────────┘
```

### Modelo de Dados MCP

#### Allowlist de Tools
**Arquivo:** `src/Meduza.Core/Entities/McpToolPolicy.cs`

```csharp
public class McpToolPolicy
{
    public Guid Id { get; set; }
    
    // Scope
    public Guid? ClientId { get; set; } // null = todas
    public Guid? SiteId { get; set; }   // null = todas
    public Guid? AgentId { get; set; }  // null = todas
    
    // Tool
    public string ToolName { get; set; } = string.Empty; // "filesystem.read_file"
    public bool IsEnabled { get; set; } = true;
    
    // Schema Validation
    public string? ArgumentSchemaJson { get; set; } // JSON Schema para validar args
    
    // Rate Limit
    public int MaxCallsPerMinute { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 10;
    
    // Audit
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
```

#### Migration
**Arquivo:** `src/Meduza.Migrations/Migrations/M046_CreateMcpToolPolicies.cs`

```csharp
[Migration(46)]
public class M046_CreateMcpToolPolicies : Migration
{
    public override void Up()
    {
        Create.Table("mcp_tool_policies")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("clients", "id")
            .WithColumn("site_id").AsGuid().Nullable().ForeignKey("sites", "id")
            .WithColumn("agent_id").AsGuid().Nullable().ForeignKey("agents", "id")
            .WithColumn("tool_name").AsString(200).NotNullable()
            .WithColumn("is_enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("argument_schema_json").AsString(int.MaxValue).Nullable()
            .WithColumn("max_calls_per_minute").AsInt32().NotNullable().WithDefaultValue(5)
            .WithColumn("timeout_seconds").AsInt32().NotNullable().WithDefaultValue(10)
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().Nullable();
            
        Create.Index("ix_mcp_tool_policies_scope")
            .OnTable("mcp_tool_policies")
            .OnColumn("client_id").Ascending()
            .OnColumn("site_id").Ascending()
            .OnColumn("agent_id").Ascending()
            .OnColumn("tool_name").Ascending();
            
        // Seed padrão: filesystem.read_file habilitada para todos
        Insert.IntoTable("mcp_tool_policies").Row(new
        {
            id = Guid.NewGuid(),
            client_id = (Guid?)null,
            site_id = (Guid?)null,
            agent_id = (Guid?)null,
            tool_name = "filesystem.read_file",
            is_enabled = true,
            argument_schema_json = """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "maxLength": 500 },
                "maxBytes": { "type": "integer", "maximum": 10240 }
              },
              "required": ["path"]
            }
            """,
            max_calls_per_minute = 5,
            timeout_seconds = 10,
            created_at = DateTime.UtcNow
        });
    }

    public override void Down()
    {
        Delete.Table("mcp_tool_policies");
    }
}
```

### Fluxo de Execução MCP

1. **OpenAI retorna tool_calls**
   - API detecta `response.ToolCalls != null`
   
2. **API valida allowlist**
   ```csharp
   var policy = await _mcpPolicyRepo.GetPolicyAsync(agentId, toolName);
   if (policy == null || !policy.IsEnabled)
       throw new UnauthorizedAccessException($"Tool {toolName} not allowed");
   ```

3. **API cria comando assinado**
   ```csharp
   var nonce = Guid.NewGuid().ToString();
   var payload = JsonSerializer.Serialize(new { tool = toolName, args, nonce });
   var signature = ComputeHmac(payload, agentToken);
   
   var command = new AgentCommand
   {
       AgentId = agentId,
       CommandType = "ExecuteMcpTool",
       Payload = payload,
       Status = CommandStatus.Pending
   };
   await _commandRepo.CreateAsync(command);
   ```

4. **Agent valida e executa**
   ```python
   # Agent Python
   def handle_execute_mcp_tool(command):
       payload = json.loads(command['payload'])
       
       # Validate signature
       expected_sig = hmac_sha256(command['payload'], agent_token)
       if payload.get('signature') != expected_sig:
           return error_result("Invalid signature")
       
       # Check allowlist (local cache)
       if payload['tool'] not in ALLOWED_TOOLS:
           return error_result(f"Tool {payload['tool']} not allowed")
       
       # Call MCP
       try:
           with timeout(10):
               result = mcp_client.call_tool(payload['tool'], payload['args'])
           
           # Sanitize output
           output = sanitize(result[:10240])  # Max 10KB
           
           return {
               'exitCode': 0,
               'output': output,
               'latencyMs': elapsed_time
           }
       except TimeoutError:
           return error_result("Tool execution timeout")
   ```

5. **API recebe resultado e re-call OpenAI**
   ```csharp
   var toolResult = await WaitForCommandResultAsync(commandId, timeout: 15);
   
   llmMessages.Add(new LlmMessage("tool", toolResult.Output, 
       toolCallId: toolCall.Id, toolName: toolCall.Name));
   
   var finalResponse = await _llmProvider.CompleteAsync(systemPrompt, llmMessages, options);
   ```

### Controles de Segurança MCP

| Controle | Implementação |
|----------|---------------|
| **Allowlist** | Tabela `mcp_tool_policies` com scope hierárquico (Client > Site > Agent) |
| **Signature** | HMAC-SHA256 do payload com AgentToken como chave |
| **Schema Validation** | JSON Schema no policy, validado antes de executar |
| **Timeout** | 10s padrão, configurável por tool |
| **Rate Limit** | Redis counter: `mcp:ratelimit:{agentId}:{toolName}:{minute}` |
| **Output Limit** | Max 10KB por call, truncado com "..." |
| **Sanitização** | Remove paths sensíveis (/etc/shadow, C:\Windows\System32\config, etc) |
| **Audit Log** | Cada tool call → LogEntry com tool, args (sanitized), latency, exitCode |
| **Nonce** | Previne replay attack (store últimos 100 nonces em Redis com TTL 5min) |

---

## Modelo de Dados Completo

### Diagrama ER

```
clients
  ├─> sites
  │     └─> agents
  │           ├─> ai_chat_sessions
  │           │     └─> ai_chat_messages
  │           ├─> ai_chat_jobs
  │           └─> agent_commands
  └─> mcp_tool_policies
```

### Resumo de Tabelas

| Tabela | Propósito | Retenção |
|--------|-----------|----------|
| `ai_chat_sessions` | Sessões de conversa | 180 dias (configurável) |
| `ai_chat_messages` | Mensagens individuais (user/assistant/tool) | Cascade com session |
| `ai_chat_jobs` | Jobs assíncronos | 90 dias |
| `mcp_tool_policies` | Allowlist de tools MCP por scope | Permanente |
| `logs` (existente) | Auditoria geral | 90 dias |

---

## Endpoints da API

### Chat Síncrono
```http
POST /api/agent-auth/me/ai-chat
Authorization: Bearer mdz_xxxxxxxxx
Content-Type: application/json

{
  "message": "Como verifico o uso de CPU?",
  "sessionId": "uuid-opcional",
  "maxTokens": 1000
}

Response 200:
{
  "sessionId": "uuid",
  "assistantMessage": "Você pode usar o comando...",
  "tokensUsed": 150,
  "conversationTokensTotal": 450,
  "latencyMs": 1234
}
```

### Chat Assíncrono
```http
POST /api/agent-auth/me/ai-chat/async
Authorization: Bearer mdz_xxxxxxxxx

{
  "message": "Analise todos os processos e identifique anomalias",
  "sessionId": "uuid-opcional",
  "maxTokens": 2000
}

Response 202:
{
  "jobId": "uuid",
  "statusUrl": "/api/agent-auth/me/ai-chat/jobs/uuid"
}
```

### Status do Job
```http
GET /api/agent-auth/me/ai-chat/jobs/{jobId}
Authorization: Bearer mdz_xxxxxxxxx

Response 200:
{
  "jobId": "uuid",
  "status": "Completed",
  "sessionId": "uuid",
  "assistantMessage": "Análise completa: ...",
  "tokensUsed": 1850,
  "createdAt": "2026-03-09T10:00:00Z",
  "completedAt": "2026-03-09T10:00:15Z"
}
```

---

## Segurança e Controles

### Matriz de Riscos e Mitigações

| Risco | Severidade | Mitigação Implementada |
|-------|-----------|------------------------|
| **API key vazada em logs** | 🔴 CRÍTICO | Sanitização regex em LoggingService, nunca logar _apiKey |
| **Agent solicita tool não autorizada** | 🔴 CRÍTICO | Allowlist em `mcp_tool_policies` + signature HMAC |
| **Replay attack de tool call** | 🟠 ALTO | Nonce único + Redis TTL 5min |
| **Prompt injection** | 🟠 ALTO | Validação de input (max 2KB, regex anti-script), system prompt hardened |
| **Agent acessa conversa de outro agent** | 🔴 CRÍTICO | WHERE agentId = authenticatedAgentId em todos os queries |
| **Tool retorna dados sensíveis** | 🟠 ALTO | Sanitização de output (remove /etc/shadow, registry SAM, etc) |
| **DoS via chat spam** | 🟡 MÉDIO | Rate limit: 10 req/min chat, 5 tool calls/min |
| **LLM retorna código malicioso** | 🟠 ALTO | Disclaimer em system prompt, user education |
| **Histórico vaza em backup** | 🟡 MÉDIO | Soft delete + hard delete após 30d grace |

### Rate Limiting

**Implementação via Redis:**
```csharp
public class AiChatRateLimiter
{
    private readonly IRedisService _redis;
    
    public async Task<bool> CheckAndIncrementAsync(Guid agentId, string action)
    {
        // action = "chat" ou "tool:filesystem.read_file"
        var key = $"ratelimit:ai:{agentId}:{action}:{DateTime.UtcNow:yyyyMMddHHmm}";
        var limit = action == "chat" ? 10 : 5;
        
        var current = await _redis.IncrementAsync(key);
        await _redis.ExpireAsync(key, TimeSpan.FromMinutes(2)); // Buffer extra
        
        return current <= limit;
    }
}
```

### Validação de Input

```csharp
private static void ValidateUserInput(string message)
{
    if (string.IsNullOrWhiteSpace(message))
        throw new ArgumentException("Message cannot be empty");
    
    if (message.Length > 2048)
        throw new ArgumentException("Message too long (max 2KB)");
    
    // Anti-injection básico
    var forbidden = new[] { "<script", "javascript:", "onerror=", "eval(" };
    if (forbidden.Any(f => message.Contains(f, StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException("Message contains forbidden patterns");
}
```

---

## Observabilidade e Auditoria

### Logs Estruturados

Cada operação gera `LogEntry` com:
- **TraceId**: correlação ponta-a-ponta (mesmo ID em agent → API → OpenAI → tool → API → agent)
- **AgentId, SiteId, ClientId**: contexto de negócio
- **Type**: "AiChat", "McpTool", "AiChatRetention"
- **Level**: Info, Warning, Error
- **DataJson**: metadata estruturada (sessionId, tokensUsed, toolName, latency, etc)

### Métricas Recomendadas

| Métrica | Onde Coletar | Alertar Se |
|---------|--------------|------------|
| Latência P95 OpenAI | AiChatService | > 5s |
| Taxa de erro OpenAI | ILlmProvider | > 5% |
| Tokens usados/dia por agent | ai_chat_messages.tokens_used | > 100k |
| Tool calls/hora | agent_commands (type=ExecuteMcpTool) | > 300 |
| Taxa de timeout MCP | CommandResult.exitCode=-1 | > 10% |
| Conversas ativas | COUNT(ai_chat_sessions WHERE closed_at IS NULL) | > 10k |

### Dashboard Operacional

```sql
-- Custo estimado por cliente (últimos 30 dias)
SELECT 
    c.name,
    SUM(m.tokens_used) AS total_tokens,
    SUM(m.tokens_used) * 0.00003 AS estimated_cost_usd  -- $0.03/1K tokens (exemplo GPT-4)
FROM ai_chat_messages m
JOIN ai_chat_sessions s ON m.session_id = s.id
JOIN clients c ON s.client_id = c.id
WHERE m.created_at > NOW() - INTERVAL '30 days'
GROUP BY c.id, c.name
ORDER BY total_tokens DESC;

-- Top 10 tools mais usadas
SELECT 
    tool_name,
    COUNT(*) AS calls,
    AVG(latency_ms) AS avg_latency,
    SUM(CASE WHEN exit_code = 0 THEN 1 ELSE 0 END)::float / COUNT(*) AS success_rate
FROM agent_commands
WHERE command_type = 'ExecuteMcpTool'
  AND created_at > NOW() - INTERVAL '7 days'
GROUP BY tool_name
ORDER BY calls DESC
LIMIT 10;
```

---

## Verificação e Testes

### Fase 1: Unit Tests
- DTOs serializam/deserializam corretamente
- Entities respeitam constraints (NOT NULL, FK, unique)
- Migration UP/DOWN sem erros

### Fase 2: Integration Tests
- OpenAiProvider retorna resposta válida (mock ou test key)
- Sanitização remove padrões "sk-proj-", "password=", "token="
- Config validation falha se ApiKey ausente em produção

### Fase 3: API Tests
- POST /me/ai-chat com token válido → 200
- POST /me/ai-chat sem token → 401
- POST /me/ai-chat com sessionId de outro agent → 403
- POST /me/ai-chat com message > 2KB → 400
- GET /me/ai-chat/jobs/{id} de outro agent → 404

### Fase 4: Retention Tests
- Criar sessão antiga (180 dias), rodar job → soft delete OK
- Criar sessão antiga + 210 dias, rodar job → hard delete OK
- Verificar cascade delete de messages

### Fase 5: Security Tests
- Forçar log de _apiKey → verificar redação
- Tentar tool não na allowlist → comando rejeitado
- Replay tool call com nonce repetido → rejeitado
- Rate limit: 11ª request em 1 minuto → 429 Too Many Requests

### Fase 6: Load Tests (opcional)
- 100 agents simultâneos, 10 req/min cada → latência P95 < 5s
- Simular 1000 tool calls em 5 min → rate limit funciona
- Verificar memory leak em 1h de chat contínuo

---

## Riscos e Mitigações

### Riscos Técnicos

1. **OpenAI API instável/lenta**
   - Mitigação: timeout 30s, retry com backoff exponencial, fallback para "Service temporarily unavailable"

2. **MCP server no agent travado**
   - Mitigação: timeout 10s, fallback para CommandResult(exitCode=-1, output="Timeout")

3. **Custo excessivo (tokens)**
   - Mitigação: maxTokens fixo, dashboard de custo, alertas > threshold

4. **Agent offline não recebe tool call**
   - Mitigação: comando fica Pending, agent busca ao reconectar, timeout 5min → Failed

### Riscos de Segurança

5. **Prompt injection avançado**
   - Mitigação: OpenAI moderation API (opcional), input validation, system prompt hardened

6. **Tool retorna segredo acidentalmente**
   - Mitigação: sanitização de output (regex /etc/shadow, SAM, etc), max 10KB truncado

7. **Agent comprometido envia tool falsa**
   - Mitigação: signature HMAC obrigatória, nonce único, allowlist estrita

---

## Considerações Futuras

### Roadmap Fase 2 (Pós-MVP)

1. **Dashboard humano para chat**
   - Interface web para técnicos conversarem com IA sobre agents
   - Contexto: agentId, siteId, histórico de tickets/comandos
   
2. **Streaming de respostas**
   - SSE (Server-Sent Events) para respostas longas
   - UX: typing indicator, partial response display
   
3. **Anexos e uploads**
   - Agent envia logs/dumps para análise IA
   - DLP scan antes de enviar para OpenAI
   
4. **Fine-tuning customizado**
   - Treinar modelo com histórico de tickets resolvidos
   - Embeddings de KB interna
   
5. **Multi-provider**
   - Suporte a Anthropic Claude, Azure OpenAI, Ollama local
   - Configuração por cliente
   
6. **Integração com Ticketing**
   - Criar ticket automático se IA detectar problema crítico
   - Anexar conversa ao ticket
   
7. **Analytics avançado**
   - Sentiment analysis de conversas
   - Topics mais frequentes
   - Satisfação do usuário (thumbs up/down)

---

## Anexos

### A. System Prompt Template

```
You are a technical support AI assistant for Meduza RMM platform.

Context:
- Agent ID: {agentId}
- Hostname: {hostname}
- OS: {osName} {osVersion}
- Site: {siteName}
- Client: {clientName}

Capabilities:
- Answer technical questions about Windows/Linux administration
- Diagnose common issues (high CPU, disk full, network problems)
- Suggest PowerShell/Bash commands (do NOT execute directly)
- Call approved tools via MCP when necessary

Constraints:
- DO NOT provide commands that delete critical system files
- DO NOT share sensitive data (passwords, API keys, tokens)
- DO NOT execute shell commands directly (use MCP tools only)
- Keep responses concise (max 500 words)
- If uncertain, ask clarifying questions

Available MCP Tools:
{toolList}
```

### B. Exemplo de Tool Policy JSON Schema

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "pattern": "^(/[^/]+)+$",
      "maxLength": 500,
      "description": "Absolute file path"
    },
    "maxBytes": {
      "type": "integer",
      "minimum": 1,
      "maximum": 10240,
      "default": 1024
    }
  },
  "required": ["path"],
  "additionalProperties": false
}
```

### C. Referências

- [OpenAI API Documentation](https://platform.openai.com/docs/api-reference)
- [Model Context Protocol Spec](https://spec.modelcontextprotocol.io/)
- [OWASP LLM Top 10](https://owasp.org/www-project-top-10-for-large-language-model-applications/)
- Meduza Docs:
  - [AUTHENTICATION_ACL_MFA_IMPLEMENTATION_PLAN.md](AUTHENTICATION_ACL_MFA_IMPLEMENTATION_PLAN.md)
  - [REPORTING_IMPLEMENTATION_PLAN.md](REPORTING_IMPLEMENTATION_PLAN.md)

---

## Changelog

| Versão | Data | Autor | Mudanças |
|--------|------|-------|----------|
| 1.0 | 2026-03-09 | Planning Agent | Documento inicial completo |

---

**FIM DO DOCUMENTO**

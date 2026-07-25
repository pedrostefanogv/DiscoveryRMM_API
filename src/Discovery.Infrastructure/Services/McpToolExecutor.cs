using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Registry e executor genérico de MCP tools autorizadas via mcp_tool_policies.
/// Substitui o KnowledgeMcpTool hardcoded — agora qualquer tool registrada na tabela
/// pode ser executada se autorizada.
/// </summary>
public interface IMcpToolExecutor
{
    /// <summary>
    /// Registra uma tool handler (invocado na inicialização).
    /// </summary>
    void RegisterHandler(string toolName, Func<McpToolCallContext, Task<string>> handler);

    /// <summary>
    /// Executa uma tool call validando a política do escopo.
    /// </summary>
    Task<string> ExecuteAsync(
        string toolName,
        string argumentsJson,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AIIntegrationSettings aiSettings,
        IReadOnlyCollection<Guid>? excludeArticleIds = null,
        Guid? departmentId = null,
        Guid? sessionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retorna a lista de LlmTool disponíveis para o escopo (para registro no LLM).
    /// </summary>
    Task<List<LlmTool>> GetAvailableToolsAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        CancellationToken ct = default);
}

public record McpToolCallContext(
    string ToolName,
    string ArgumentsJson,
    JsonDocument Arguments,
    Guid? ClientId,
    Guid? SiteId,
    Guid? AgentId,
    McpToolPolicy Policy,
    AIIntegrationSettings AiSettings,
    IReadOnlyCollection<Guid>? ExcludeArticleIds,
    Guid? DepartmentId,
    Guid? SessionId,
    CancellationToken CancellationToken);

public class McpToolExecutor : IMcpToolExecutor
{
    private readonly IMcpToolPolicyRepository _policyRepo;
    private readonly IKnowledgeMcpTool _knowledgeMcpTool;
    private readonly IAiChatMessageRepository _messageRepo;
    private readonly ILogger<McpToolExecutor> _logger;
    private readonly ConcurrentDictionary<string, Func<McpToolCallContext, Task<string>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _rateLimitCounters = new(StringComparer.OrdinalIgnoreCase);

    public McpToolExecutor(
        IMcpToolPolicyRepository policyRepo,
        IKnowledgeMcpTool knowledgeMcpTool,
        IAiChatMessageRepository messageRepo,
        ILogger<McpToolExecutor> logger)
    {
        _policyRepo = policyRepo;
        _knowledgeMcpTool = knowledgeMcpTool;
        _messageRepo = messageRepo;
        _logger = logger;

        // Handler padrão: knowledge_search (backward compat)
        RegisterHandler("knowledge_search", HandleKnowledgeSearchAsync);
        RegisterHandler("time.current", ctx => HandleTimeCurrentAsync(ctx));
        RegisterHandler("sequential_thinking", ctx => HandleSequentialThinkingAsync(ctx));
        RegisterHandler("memory.search", HandleMemorySearchAsync);
    }

    public void RegisterHandler(string toolName, Func<McpToolCallContext, Task<string>> handler)
    {
        _handlers[toolName] = handler;
        _logger.LogInformation("MCP tool handler registrado: {ToolName}", toolName);
    }

    public async Task<string> ExecuteAsync(
        string toolName,
        string argumentsJson,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AIIntegrationSettings aiSettings,
        IReadOnlyCollection<Guid>? excludeArticleIds = null,
        Guid? departmentId = null,
        Guid? sessionId = null,
        CancellationToken ct = default)
    {
        // 1. Validar política
        var policy = await _policyRepo.GetPolicyAsync(toolName, clientId, siteId, agentId, ct);

        if (policy is null)
        {
            _logger.LogWarning("MCP tool '{ToolName}' não tem política registrada para o escopo", toolName);
            return $"{{\"error\": \"Tool '{toolName}' não autorizada para este escopo.\"}}";
        }

        if (!policy.IsEnabled)
        {
            _logger.LogWarning("MCP tool '{ToolName}' desabilitada para o escopo", toolName);
            return $"{{\"error\": \"Tool '{toolName}' está desabilitada.\"}}";
        }

        // 2. Rate limiting
        var rateKey = $"{toolName}:{clientId}:{siteId}:{agentId}";
        var currentCount = _rateLimitCounters.AddOrUpdate(rateKey, 1, (_, v) => v + 1);

        // Reset simples (não é sliding window, mas evita a necessidade de Redis para este caso)
        _ = Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(t =>
        {
            _rateLimitCounters.TryRemove(rateKey, out var removed);
        });

        if (currentCount > policy.MaxCallsPerMinute)
        {
            _logger.LogWarning("MCP tool '{ToolName}' excedeu limite de {Max}/min", toolName, policy.MaxCallsPerMinute);
            return $"{{\"error\": \"Rate limit excedido para tool '{toolName}'. Máximo: {policy.MaxCallsPerMinute}/min.\"}}";
        }

        // 3. Buscar handler
        if (!_handlers.TryGetValue(toolName, out var handler))
        {
            _logger.LogWarning("MCP tool '{ToolName}' sem handler registrado", toolName);
            return $"{{\"error\": \"Tool '{toolName}' não tem implementação registrada.\"}}";
        }

        // 4. Executar com timeout
        try
        {
            using var argsDoc = JsonDocument.Parse(argumentsJson);
            var context = new McpToolCallContext(
                toolName, argumentsJson, argsDoc,
                clientId, siteId, agentId,
                policy, aiSettings, excludeArticleIds,
                departmentId, sessionId, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(policy.TimeoutSeconds));

            var result = await handler(context);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("MCP tool '{ToolName}' timeout após {Timeout}s", toolName, policy.TimeoutSeconds);
            return $"{{\"error\": \"Tool '{toolName}' excedeu timeout de {policy.TimeoutSeconds}s.\"}}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao executar MCP tool '{ToolName}'", toolName);
            return $"{{\"error\": \"Erro interno ao executar tool '{toolName}'.\"}}";
        }
    }

    public async Task<List<LlmTool>> GetAvailableToolsAsync(
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        CancellationToken ct = default)
    {
        var policies = await _policyRepo.GetEffectivePoliciesAsync(clientId, siteId, agentId, ct);

        return policies
            .Where(p => p.IsEnabled && _handlers.ContainsKey(p.ToolName))
            .Select(p => BuildLlmTool(p))
            .ToList();
    }

    private static LlmTool BuildLlmTool(McpToolPolicy policy)
    {
        object schema;
        try
        {
            schema = !string.IsNullOrWhiteSpace(policy.ArgumentSchemaJson)
                ? JsonSerializer.Deserialize<object>(policy.ArgumentSchemaJson)!
                : new { type = "object", properties = new { }, required = Array.Empty<string>() };
        }
        catch
        {
            schema = new { type = "object", properties = new { }, required = Array.Empty<string>() };
        }

        return new LlmTool(
            Name: policy.ToolName,
            Description: GetToolDescription(policy.ToolName),
            Schema: schema);
    }

    private static string GetToolDescription(string toolName) => toolName switch
    {
        "knowledge_search" => "Pesquisa artigos e procedimentos na base de conhecimento corporativa. Use quando o usuário perguntar sobre políticas, SOPs, sistemas internos, procedimentos de TI ou qualquer assunto documentado da empresa. O parâmetro 'query' é OBRIGATÓRIO e deve conter os termos de busca (ex: 'configurar VPN', 'política de senhas', 'instalar impressora HP'). NÃO use para perguntas genéricas de informática que não envolvam sistemas/procedimentos internos da empresa — para essas, responda com seu próprio conhecimento.",

        "filesystem.read_file" => "Lê o conteúdo de um arquivo do sistema de arquivos do computador do usuário. O parâmetro 'path' é OBRIGATÓRIO e deve ser o caminho absoluto do arquivo (ex: 'C:\\Users\\usuario\\Documents\\config.ini'). NÃO use para listar diretórios, escrever ou modificar arquivos — apenas leitura. Use com cautela e apenas sob demanda explícita do usuário.",

        "postgres.query" => "Executa consultas SQL read-only (SELECT) na base de dados do Discovery RMM para gerar relatórios e análises ad-hoc. O parâmetro 'sql' é OBRIGATÓRIO. NÃO use para INSERT, UPDATE, DELETE ou qualquer operação de escrita — apenas SELECT. Use APENAS quando o usuário solicitar dados estruturados que não estão disponíveis via knowledge_search (ex: 'quantos chamados foram abertos este mês?', 'liste os computadores sem antivírus').",

        "time.current" => "Retorna data/hora atual em UTC e horário local (America/Sao_Paulo), além do timestamp Unix. Útil para cálculos de SLA, verificação de prazos e contexto temporal. NÃO requer parâmetros — basta invocar a função. NÃO use para agendar tarefas ou definir alarmes.",

        "memory.search" => "Pesquisa informações salvas em conversas anteriores com este usuário/máquina (memória persistente). Use no INÍCIO de cada conversa para reconhecer o perfil do usuário, preferências e problemas anteriores. O parâmetro 'query' é OBRIGATÓRIO (ex: 'preferências do usuário', 'problemas anteriores com impressora'). NÃO use para buscar artigos da base de conhecimento — para isso use 'knowledge_search'.",

        "sequential_thinking" => "Executa raciocínio estruturado multi-step para diagnosticar problemas complexos de TI. Use APENAS quando o problema exigir análise em etapas (ex: diagnosticar por que um computador está lento considerando CPU, memória, disco e rede). O parâmetro 'thought' é OBRIGATÓRIO e deve conter o raciocínio do passo atual. NÃO use para perguntas simples ou respostas diretas — apenas para diagnósticos que exigem múltiplas camadas de análise.",

        _ => $"Executa a tool '{toolName}'."
    };

    // ── Handlers padrão ──────────────────────────────────────────────────────

    private async Task<string> HandleKnowledgeSearchAsync(McpToolCallContext ctx)
    {
        var query = ctx.Arguments.RootElement.TryGetProperty("query", out var qProp)
            ? qProp.GetString() ?? string.Empty
            : string.Empty;
        var maxRes = ctx.Arguments.RootElement.TryGetProperty("max_results", out var mProp)
            ? mProp.GetInt32()
            : 3;

        return await _knowledgeMcpTool.ExecuteWithSettingsAsync(
            ctx.ClientId,
            ctx.SiteId,
            query,
            ctx.AiSettings,
            excludeArticleIds: ctx.ExcludeArticleIds,
            maxResults: maxRes,
            departmentId: ctx.DepartmentId,
            ct: ctx.CancellationToken);
    }

    private static Task<string> HandleTimeCurrentAsync(McpToolCallContext ctx)
    {
        var now = DateTimeOffset.UtcNow;
        var local = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, "E. South America Standard Time");
        var result = new
        {
            utc = now.ToString("O"),
            local = local.ToString("O"),
            timezone = "America/Sao_Paulo",
            unix_timestamp = now.ToUnixTimeSeconds()
        };
        return Task.FromResult(JsonSerializer.Serialize(result));
    }

    private static Task<string> HandleSequentialThinkingAsync(McpToolCallContext ctx)
    {
        // No-op: registra o pensamento no log para debug/trace, mas não afeta a execução.
        var thought = ctx.Arguments.RootElement.TryGetProperty("thought", out var tProp)
            ? tProp.GetString() ?? string.Empty
            : string.Empty;
        var step = ctx.Arguments.RootElement.TryGetProperty("step", out var sProp)
            ? sProp.GetInt32() : 0;

        var result = new
        {
            acknowledged = true,
            step,
            message = $"Pensamento registrado no passo {step}. Continue a análise."
        };
        return Task.FromResult(JsonSerializer.Serialize(result));
    }

    private async Task<string> HandleMemorySearchAsync(McpToolCallContext ctx)
    {
        var query = ctx.Arguments.RootElement.TryGetProperty("query", out var qProp)
            ? qProp.GetString() ?? string.Empty
            : string.Empty;
        var maxResults = ctx.Arguments.RootElement.TryGetProperty("max_results", out var mrProp)
            ? mrProp.GetInt32()
            : 5;

        if (!ctx.SessionId.HasValue)
        {
            return JsonSerializer.Serialize(new
            {
                results = Array.Empty<object>(),
                message = "SessionId não disponível para busca em memória."
            });
        }

        try
        {
            var messages = await _messageRepo.GetRecentBySessionAsync(
                ctx.SessionId.Value,
                limit: 50,
                ct: ctx.CancellationToken);

            var matches = messages
                .Where(m => !string.IsNullOrWhiteSpace(m.Content) &&
                            m.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.SequenceNumber)
                .Take(maxResults)
                .Select(m => new
                {
                    role = m.Role,
                    content = m.Content.Length > 500 ? m.Content[..500] + "..." : m.Content,
                    sequence = m.SequenceNumber,
                    created_at = m.CreatedAt.ToString("O")
                })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                results = matches,
                total = matches.Count,
                query
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao buscar memória para SessionId={SessionId}", ctx.SessionId);
            return JsonSerializer.Serialize(new
            {
                results = Array.Empty<object>(),
                error = "Erro ao buscar na memória da conversa."
            });
        }
    }
}

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
    CancellationToken CancellationToken);

public class McpToolExecutor : IMcpToolExecutor
{
    private readonly IMcpToolPolicyRepository _policyRepo;
    private readonly IKnowledgeMcpTool _knowledgeMcpTool;
    private readonly ILogger<McpToolExecutor> _logger;
    private readonly ConcurrentDictionary<string, Func<McpToolCallContext, Task<string>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _rateLimitCounters = new(StringComparer.OrdinalIgnoreCase);

    public McpToolExecutor(
        IMcpToolPolicyRepository policyRepo,
        IKnowledgeMcpTool knowledgeMcpTool,
        ILogger<McpToolExecutor> logger)
    {
        _policyRepo = policyRepo;
        _knowledgeMcpTool = knowledgeMcpTool;
        _logger = logger;

        // Handler padrão: knowledge_search (backward compat)
        RegisterHandler("knowledge_search", HandleKnowledgeSearchAsync);
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
                departmentId, ct);

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
        "knowledge_search" => "Pesquisa artigos e procedimentos na base de conhecimento da empresa. Use quando o usuário perguntar sobre procedimentos, políticas, SOPs ou quando precisar de informações específicas documentadas.",
        "filesystem.read_file" => "Lê o conteúdo de um arquivo do sistema de arquivos do agent. Use com cautela e apenas quando autorizado.",
        "postgres.query" => "Executa query SQL read-only na base do Discovery RMM para gerar relatórios ad-hoc. Somente SELECT é permitido.",
        "time.current" => "Retorna data/hora atual em UTC e local, útil para cálculos de SLA e prazos.",
        "memory.search" => "Pesquisa na memória persistente do chat por informações salvas anteriormente.",
        "sequential_thinking" => "Executa raciocínio multi-step para diagnosticar problemas complexos de forma estruturada.",
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
}

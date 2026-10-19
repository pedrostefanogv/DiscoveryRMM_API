using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Cqrs.AgentLabels.Queries;
using Discovery.Core.Cqrs.Alerts.Queries;
using Discovery.Core.Cqrs.MonitoringEvents.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using MediatR;
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

    /// <summary>
    /// Catálogo das tools de SERVIDOR com handler registrado (nome + descrição).
    /// Usado pela tela de governança para listar o que pode ser habilitado.
    /// </summary>
    IReadOnlyList<(string Name, string Description)> GetServerCatalog();

    /// <summary>
    /// Indica se a tool/capacidade está HABILITADA no escopo (herança incluída).
    /// Ausência de política = habilitada (mesmo default da governança). Usado
    /// para gate de CAPACIDADES que não são tools executáveis — hoje a A2UI.
    /// </summary>
    Task<bool> IsToolEnabledAsync(
        string toolName,
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
    private readonly IKnowledgeListMcpTool _knowledgeListMcpTool;
    private readonly IAiChatMessageRepository _messageRepo;
    private readonly IMediator _mediator;
    private readonly ILogger<McpToolExecutor> _logger;
    private readonly ConcurrentDictionary<string, Func<McpToolCallContext, Task<string>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _rateLimitCounters = new(StringComparer.OrdinalIgnoreCase);

    public McpToolExecutor(
        IMcpToolPolicyRepository policyRepo,
        IKnowledgeMcpTool knowledgeMcpTool,
        IKnowledgeListMcpTool knowledgeListMcpTool,
        IAiChatMessageRepository messageRepo,
        IMediator mediator,
        ILogger<McpToolExecutor> logger)
    {
        _policyRepo = policyRepo;
        _knowledgeMcpTool = knowledgeMcpTool;
        _knowledgeListMcpTool = knowledgeListMcpTool;
        _messageRepo = messageRepo;
        _mediator = mediator;
        _logger = logger;

        // Handlers padrão da KB: busca por assunto + catálogo.
        RegisterHandler("knowledge_search", HandleKnowledgeSearchAsync);
        RegisterHandler("knowledge_list", HandleKnowledgeListAsync);
        RegisterHandler("time.current", ctx => HandleTimeCurrentAsync(ctx));
        RegisterHandler("sequential_thinking", ctx => HandleSequentialThinkingAsync(ctx));
        RegisterHandler("memory.search", HandleMemorySearchAsync);

        // Tools de contexto operacional do SERVIDOR (leitura). Ficam sujeitas à
        // governança de /settings/mcp-tools como qualquer outra tool: sem policy
        // global a execução é negada, por isso a M236 faz o seed.
        RegisterHandler("get_agent_alerts", HandleGetAgentAlertsAsync);
        RegisterHandler("get_agent_labels", HandleGetAgentLabelsAsync);
        RegisterHandler("get_agent_metrics_history", HandleGetAgentMetricsHistoryAsync);
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
            // Timeout <= 0 = sem limite (a tool fica só sob o cancelamento do turno).
            if (policy.TimeoutSeconds > 0)
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

    public async Task<bool> IsToolEnabledAsync(
        string toolName,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return true;
        var policy = await _policyRepo.GetPolicyAsync(toolName, clientId, siteId, agentId, ct);
        return policy?.IsEnabled ?? true;
    }

    public IReadOnlyList<(string Name, string Description)> GetServerCatalog() =>
        _handlers.Keys
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => (n, GetToolDescription(n)))
            .ToList();

    private static string GetToolDescription(string toolName) => toolName switch
    {
        "knowledge_search" => "Pesquisa artigos e procedimentos na base de conhecimento corporativa. Use quando o usuário perguntar sobre políticas, SOPs, sistemas internos, procedimentos de TI ou qualquer assunto documentado da empresa. O parâmetro 'query' é OBRIGATÓRIO e deve conter os termos de busca (ex: 'configurar VPN', 'política de senhas', 'instalar impressora HP'). NÃO use para perguntas genéricas de informática que não envolvam sistemas/procedimentos internos da empresa — para essas, responda com seu próprio conhecimento. Cada resultado traz 'internal_url' (discovery://knowledge/article/<id>) para abrir o artigo direto no app.",

        "knowledge_list" => "Lista os artigos PUBLICADOS da base de conhecimento acessíveis a esta máquina (id, título, categoria, escopo, tags, resumo e internal_url). Use SEMPRE que o usuário perguntar quais artigos/procedimentos existem, o que a base contém, ou quando precisar confirmar se há conteúdo sobre um assunto antes de dizer que não encontrou. NÃO exige parâmetros; opcionalmente filtre por 'category'. Para procurar o conteúdo de um assunto específico use knowledge_search. Cada item traz 'internal_url' (discovery://knowledge/article/<id>) para abrir o artigo direto no app.",

        "time.current" => "Retorna data/hora atual em UTC e horário local (America/Sao_Paulo), além do timestamp Unix. Útil para cálculos de SLA, verificação de prazos e contexto temporal. NÃO requer parâmetros — basta invocar a função. NÃO use para agendar tarefas ou definir alarmes.",

        "memory.search" => "Pesquisa no histórico PERSISTENTE das conversas ANTERIORES desta máquina (servidor) e devolve trechos de mensagens que contenham os termos. Use no INÍCIO de uma conversa NOVA para recordar o contexto, preferências e problemas já tratados neste computador. O parâmetro 'query' é OBRIGATÓRIO e deve conter palavras-chave do relato (ex: 'impressora', 'VPN', 'lentidão', 'senha'). A busca IGNORA a conversa atual — o histórico dela já está no contexto. NÃO use para buscar artigos da base de conhecimento (use 'knowledge_search') nem confunda com 'memory_list'/'memory_create': essas gravam ANOTAÇÕES LOCAIS no computador do usuário; esta lê o histórico de conversas no servidor.",

        "get_agent_alerts" => "Lê NO SERVIDOR os alertas/notificações PSADT vinculados a esta máquina (histórico, não o estado instantâneo). Use quando o usuário perguntar por que o computador está alertando, quais avisos recebeu, se algum alerta está ligado a um chamado, ou ao investigar avisos que o usuário mencionou. SOMENTE LEITURA: não cria, agenda, dispara nem cancela alertas. Parâmetros opcionais: 'status' (Draft, Scheduled, Dispatching, Dispatched, Expired, Cancelled), 'ticketId', 'cursor' e 'limit' (1–20, padrão 20). O escopo (agente/cliente/site) vem SEMPRE do contexto da conversa — nunca informe IDs de escopo; valores desconhecidos de status retornam erro claro.",

        "get_agent_labels" => "Lê NO SERVIDOR as labels aplicadas a esta máquina (automáticas por regras do tenant e manuais), com a regra que as originou quando identificável. Use para explicar por que a máquina está marcada (ex.: PROD, NOTURNO, SEM-ANTIVIRUS) e para entender a classificação operacional antes de diagnosticar sintomas. SOMENTE LEITURA e sem parâmetros; exige contexto de máquina (agentId). Não lista regras de outros agentes nem altera labels — para isso use o console de governança.",

        "get_agent_metrics_history" => "Lê NO SERVIDOR o histórico de eventos de monitoramento desta máquina (alertas de CPU, memória, disco, serviços etc.), com severidade, origem, data e resumo por severidade/origem. Use para TENDÊNCIA e picos ('CPU alta ontem entre 13h e 15h'), enquanto get_performance_snapshot mostra apenas o instante atual. SOMENTE LEITURA. Parâmetros opcionais: 'limit' (1–200, padrão 50) e 'cursor' para a página seguinte. A resposta indica 'truncated' quando há mais eventos; reduza o limit ou avance o cursor em vez de presumir que a lista está completa.",

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

    private async Task<string> HandleKnowledgeListAsync(McpToolCallContext ctx)
    {
        var root = ctx.Arguments.RootElement;

        var category = root.TryGetProperty("category", out var cProp) && cProp.ValueKind == JsonValueKind.String
            ? cProp.GetString()
            : null;

        var limit = root.TryGetProperty("limit", out var lProp) && lProp.ValueKind == JsonValueKind.Number
            ? lProp.GetInt32()
            : 50;

        return await _knowledgeListMcpTool.ExecuteAsync(
            ctx.ClientId, ctx.SiteId, category, limit, ctx.CancellationToken);
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

    /// <summary>
    /// Memória persistente: busca o termo nas conversas ANTERIORES da mesma
    /// máquina (agent_id), excluindo a sessão atual.
    ///
    /// Antes esta tool procurava apenas nas 50 mensagens mais recentes da PRÓPRIA
    /// sessão — exatamente o histórico que o LLM já tem no contexto. Com isso ela
    /// (a) nunca cumpria a promessa do prompt ("recordar problemas anteriores
    /// desta máquina") em conversas novas e (b) colidia com as tools de memória do
    /// agente (memory_list/memory_create/memory_delete, anotações locais),
    /// confundindo o modelo com dois conceitos de "memória" para o mesmo nome.
    /// </summary>
    private async Task<string> HandleMemorySearchAsync(McpToolCallContext ctx)
    {
        // Extração tolerante: o LLM às vezes manda tipo errado ("query": 123,
        // "max_results": "5"). GetString()/GetInt32() sem checar ValueKind
        // lançava InvalidOperationException ANTES do try e virava erro interno.
        var query = ctx.Arguments.RootElement.TryGetProperty("query", out var qProp)
                    && qProp.ValueKind == JsonValueKind.String
            ? qProp.GetString() ?? string.Empty
            : string.Empty;

        var maxResults = ctx.Arguments.RootElement.TryGetProperty("max_results", out var mrProp)
                         && mrProp.ValueKind == JsonValueKind.Number
                         && mrProp.TryGetInt32(out var parsedMax)
            ? parsedMax
            : 5;

        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonSerializer.Serialize(new
            {
                results = Array.Empty<object>(),
                total = 0,
                message = "Informe 'query' com as palavras-chave a recordar (ex: 'impressora', 'VPN')."
            });
        }

        if (!ctx.AgentId.HasValue)
        {
            return JsonSerializer.Serialize(new
            {
                results = Array.Empty<object>(),
                total = 0,
                message = "Memória de conversas disponível apenas no contexto de uma máquina (agentId)."
            });
        }

        try
        {
            var matches = await _messageRepo.SearchByAgentAsync(
                ctx.AgentId.Value,
                query,
                maxResults,
                excludeSessionId: ctx.SessionId,
                ct: ctx.CancellationToken);

            var results = matches.Select(m =>
            {
                // Coluna content pode ser NULL em linhas antigas; sem a guarda o
                // Select estoura NRE e a tool devolve erro genérico.
                var text = m.Content ?? string.Empty;
                return new
                {
                    role = m.Role,
                    content = text.Length > 500 ? text[..500] + "..." : text,
                    session_id = m.SessionId,
                    created_at = m.CreatedAt.ToString("O")
                };
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                results,
                total = results.Count,
                query,
                scope = "conversas anteriores desta máquina"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao buscar memória persistente para AgentId={AgentId}", ctx.AgentId);
            return JsonSerializer.Serialize(new
            {
                results = Array.Empty<object>(),
                total = 0,
                error = "Erro ao buscar no histórico de conversas."
            });
        }
    }

    // ── Tools de contexto operacional do SERVIDOR (leitura) ──────────────────
    //
    // Regras de segurança comuns a estas três tools:
    //  • o escopo (clientId/siteId/agentId) vem SEMPRE de McpToolCallContext;
    //    nenhum ID de escopo é lido dos argumentos enviados pelo LLM;
    //  • exigem ctx.AgentId e devolvem erro claro quando a conversa não tem
    //    contexto de máquina;
    //  • o payload é limitado (limit curto + flag de truncamento);
    //  • nenhuma exceção escapa: erros viram {"error": "..."} em JSON. A única
    //    exceção é OperationCanceledException, repassada para o executor
    //    converter no erro padrão de timeout.

    private async Task<string> HandleGetAgentAlertsAsync(McpToolCallContext ctx)
    {
        if (!ctx.AgentId.HasValue)
            return ErrorJson("Alertas da máquina só estão disponíveis no contexto de um agente (agentId ausente).");

        try
        {
            var root = ctx.Arguments.RootElement;

            var status = GetStringArg(root, "status");
            if (!string.IsNullOrWhiteSpace(status)
                && !Enum.TryParse<AlertDefinitionStatus>(status, ignoreCase: true, out _))
            {
                return ErrorJson(
                    "Status inválido. Use Draft, Scheduled, Dispatching, Dispatched, Expired ou Cancelled.");
            }

            Guid? ticketId = null;
            if (root.TryGetProperty("ticketId", out var ticketProp)
                && ticketProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(ticketProp.GetString()))
            {
                if (!Guid.TryParse(ticketProp.GetString(), out var parsedTicket))
                    return ErrorJson("'ticketId' inválido: informe o GUID do chamado ou omita o campo.");
                ticketId = parsedTicket;
            }

            var cursor = GetStringArg(root, "cursor");
            // Teto de 20 acompanha o argument_schema_json da M236.
            var limit = Math.Clamp(GetIntArg(root, "limit", 20), 1, 20);

            var result = await _mediator.Send(
                new ListAgentAlertsQuery(
                    status,
                    nameof(AlertScopeType.Agent),
                    ctx.ClientId,
                    ctx.SiteId,
                    ctx.AgentId,
                    ticketId,
                    cursor,
                    limit,
                    IncludeParentScopes: true),
                ctx.CancellationToken);

            if (result.IsFailure || result.Value is null)
            {
                var message = result.Errors.Count > 0
                    ? string.Join(" ", result.Errors.Select(e => e.Message))
                    : "Falha ao consultar os alertas.";
                return ErrorJson(message);
            }

            var data = result.Value;
            var alerts = data.Items.Select(alert => new
            {
                id = alert.Id,
                status = alert.Status,
                severity = alert.Severity,
                // Títulos podem conter e-mail/usuário; mascara e-mail para não
                // vazar PII no contexto do LLM.
                title = RedactPii(alert.Title),
                createdAt = alert.CreatedAt,
                // A entidade AgentAlertDefinition não possui data de resolução
                // (o ciclo termina em Expired/Cancelled); o campo é mantido no
                // contrato para estabilidade e vem sempre nulo hoje.
                resolvedAt = (DateTime?)null,
                ticketId = alert.TicketId
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                alerts,
                total = data.Total,
                hasMore = data.HasMore,
                nextCursor = data.NextCursor,
                truncated = data.HasMore,
                scope = "agente desta conversa",
                note = data.HasMore
                    ? "Há mais alertas; use 'cursor' para a próxima página."
                    : null
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao consultar alertas via MCP para AgentId={AgentId}", ctx.AgentId);
            return ErrorJson("Não foi possível consultar os alertas da máquina agora.");
        }
    }

    private async Task<string> HandleGetAgentLabelsAsync(McpToolCallContext ctx)
    {
        if (!ctx.AgentId.HasValue)
            return ErrorJson("Labels da máquina só estão disponíveis no contexto de um agente (agentId ausente).");

        try
        {
            var labelsResult = await _mediator.Send(
                new ListAgentLabelsQuery(ctx.AgentId.Value), ctx.CancellationToken);

            if (labelsResult.IsFailure || labelsResult.Value is null)
            {
                var message = labelsResult.Errors.Count > 0
                    ? string.Join(" ", labelsResult.Errors.Select(e => e.Message))
                    : "Falha ao consultar as labels.";
                return ErrorJson(message);
            }

            // Nome da regra que originou a label: o DTO de labels não carrega o
            // vínculo, então cruzamos com as regras por Label. É informação
            // complementar — se a consulta de regras falhar, devolvemos as
            // labels sem o nome da regra em vez de falhar a tool inteira.
            var ruleByLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var rulesResult = await _mediator.Send(new ListLabelRulesQuery(), ctx.CancellationToken);
                if (rulesResult.IsSuccess && rulesResult.Value is not null)
                {
                    foreach (var rule in rulesResult.Value)
                    {
                        if (!string.IsNullOrWhiteSpace(rule.Label))
                            ruleByLabel.TryAdd(rule.Label, rule.Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Não foi possível cruzar labels com regras (AgentId={AgentId})", ctx.AgentId);
            }

            var labels = labelsResult.Value.Select(label => new
            {
                label = label.Label,
                source = label.SourceType,
                createdAt = label.CreatedAt,
                ruleName = ruleByLabel.TryGetValue(label.Label, out var ruleName) ? ruleName : null
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                labels,
                count = labels.Count,
                scope = "agente desta conversa"
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao consultar labels via MCP para AgentId={AgentId}", ctx.AgentId);
            return ErrorJson("Não foi possível consultar as labels da máquina agora.");
        }
    }

    private async Task<string> HandleGetAgentMetricsHistoryAsync(McpToolCallContext ctx)
    {
        if (!ctx.AgentId.HasValue)
            return ErrorJson("Histórico de métricas só está disponível no contexto de um agente (agentId ausente).");

        try
        {
            var root = ctx.Arguments.RootElement;
            var cursor = GetStringArg(root, "cursor");
            // Máximo 200: acima disso o payload degrada o contexto do chat.
            var limit = Math.Clamp(GetIntArg(root, "limit", 50), 1, 200);

            var result = await _mediator.Send(
                new ListMonitoringEventsQuery(ctx.AgentId, ctx.ClientId, ctx.SiteId, cursor, limit),
                ctx.CancellationToken);

            if (result.IsFailure || result.Value is null)
            {
                var message = result.Errors.Count > 0
                    ? string.Join(" ", result.Errors.Select(e => e.Message))
                    : "Falha ao consultar o histórico de eventos.";
                return ErrorJson(message);
            }

            var events = result.Value;
            var items = events.Select(evt => new
            {
                id = evt.Id,
                alertCode = evt.AlertCode,
                severity = evt.Severity,
                source = evt.Source,
                createdAt = evt.CreatedAt,
                labels = Truncate(evt.Labels, 500)
            }).ToList();

            // GroupBy com chave nula faria ToDictionary lançar e derrubar a tool
            // inteira: normaliza para "unknown".
            var bySeverity = events
                .GroupBy(e => string.IsNullOrWhiteSpace(e.Severity) ? "unknown" : e.Severity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var bySource = events
                .GroupBy(e => string.IsNullOrWhiteSpace(e.Source) ? "unknown" : e.Source, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            // truncated conservador: se a página veio cheia, pode haver mais.
            var truncated = events.Count >= limit;
            // Cursor keyset (CreatedAt + Id) da última linha da página: o
            // handler de /monitoring-events devolve só a lista, então a tool
            // calcula o cursor para permitir a página seguinte.
            var lastEvent = events.Count > 0 ? events[^1] : null;
            var nextCursor = truncated && lastEvent is not null
                ? CursorPaginationHelper.EncodeCreatedAtCursor(lastEvent.CreatedAt, lastEvent.Id)
                : null;

            return JsonSerializer.Serialize(new
            {
                events = items,
                count = items.Count,
                limit,
                truncated,
                nextCursor,
                summary = new { bySeverity, bySource },
                scope = "agente desta conversa",
                note = truncated
                    ? "A página veio cheia: pode haver eventos mais antigos. Reduza 'limit' ou refine o período no console."
                    : null
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao consultar histórico de métricas via MCP para AgentId={AgentId}", ctx.AgentId);
            return ErrorJson("Não foi possível consultar o histórico de métricas da máquina agora.");
        }
    }

    private static string ErrorJson(string message) => JsonSerializer.Serialize(new { error = message });

    /// <summary>Extrai string de argumento tolerando tipo errado enviado pelo LLM.</summary>
    private static string? GetStringArg(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>Extrai inteiro de argumento tolerando tipo errado enviado pelo LLM.</summary>
    private static int GetIntArg(JsonElement root, string name, int fallback)
        => root.TryGetProperty(name, out var prop)
           && prop.ValueKind == JsonValueKind.Number
           && prop.TryGetInt32(out var value)
            ? value
            : fallback;

    /// <summary>Mascara e-mails em textos livres (títulos de alerta) antes de expor ao LLM.</summary>
    private static string RedactPii(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(
                text, @"[\w.+-]+@[\w-]+\.[\w.-]+", "[email]");

    /// <summary>Corta textos longos (snapshot de labels) para não estourar o payload.</summary>
    private static string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }
}

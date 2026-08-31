using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Orquestrador de tool calls: registro, enriquecimento, validação,
/// hints de erro imperativos, XML fallback e reconstrução de histórico.
/// </summary>
public class AiChatToolOrchestrator
{
    private readonly IMemoryCache _cache;
    private readonly IMcpToolExecutor _mcpToolExecutor;
    private readonly ILogger<AiChatService> _logger;

    private static readonly Regex XmlToolCallRegex = new(
        @"<(\w+)>\s*(\{[^}]*\})\s*</\w+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    // Detecta a marcação DSML nativa do modelo (DeepSeek) — o LLM às vezes
    // emite tool calls nessa marcação em vez de function call nativa. O
    // separador ｜ é U+FF5C (fullwidth); também aceita | ASCII. O nome da
    // seção (ex.: tool_invokes) é opcional. Usada apenas para LOG de
    // diagnóstico — a remoção do texto é feita pelo AiChatLeakSanitizer.
    private static readonly Regex DsmlDiagnosticRegex = new(
        @"<[/]?[｜|]DSML[｜|][a-z_]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

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

    // ── Fonte única de verdade para hints de "argumentos vazios" ─────────────
    // Evita drift entre os 3 pontos que emitem hints de erro (antes havia 3
    // switches duplicados com textos levemente diferentes, bug de packageId).
    private const string EmptyArgHintDefault = "VOCÊ ENVIOU PARÂMETROS VAZIOS. Leia o histórico, extraia os valores corretos e tente novamente AGORA.";
    private const string EmptyArgHintDefaultAscii = "VOCE ENVIOU PARAMETROS VAZIOS. Leia o historico, extraia os valores corretos e tente novamente AGORA.";

    private static readonly Dictionary<string, string> EmptyArgHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search_packages"] = "VOCÊ CHAMOU search_packages COM query VAZIA. ISSO É UM ERRO GRAVE. Leia a mensagem do usuário no histórico e extraia o nome do programa. Se o usuário disse \"Quero instalar o Foxit\", você DEVE chamar search_packages com query=\"Foxit\". NÃO desista. NÃO mude de assunto. NÃO pergunte ao usuário o que ele quer — ele JÁ disse. CORRIJA o parâmetro query e chame search_packages novamente AGORA.",
        ["ask_user"] = "VOCÊ CHAMOU ask_user COM question VAZIA. Leia o histórico da conversa e formule uma pergunta clara baseada no contexto.",
        ["create_ticket"] = "VOCÊ CHAMOU create_ticket COM PARÂMETROS VAZIOS. Extraia do histórico: título, descrição, categoria e prioridade. NÃO pergunte ao usuário — ele JÁ forneceu as informações.",
        ["install_package"] = "VOCÊ CHAMOU install_package COM PARÂMETROS VAZIOS. Você precisa do 'id' exato — execute search_packages PRIMEIRO para obter o ID correto do programa. NUNCA invente um ID. Se ainda não executou search_packages, faça isso AGORA.",
    };

    private static readonly Dictionary<string, string> EmptyArgHintsAscii = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search_packages"] = "VOCE CHAMOU search_packages COM query VAZIA. Leia a mensagem do usuario no historico e extraia o nome do programa. Se o usuario disse \"Quero instalar o Adobe Acrobat\", voce DEVE chamar search_packages com query=\"Adobe Acrobat\". NAO desista. CORRIJA o parametro query e chame search_packages novamente AGORA.",
        ["ask_user"] = "VOCE CHAMOU ask_user COM question VAZIA. Leia o historico e formule uma pergunta clara.",
        ["create_ticket"] = "VOCE CHAMOU create_ticket COM PARAMETROS VAZIOS. Extraia do historico: titulo, descricao, categoria e prioridade.",
        ["install_package"] = "VOCE CHAMOU install_package COM PARAMETROS VAZIOS. Extraia do historico o nome/id do programa.",
    };

    public static string GetEmptyArgHint(string toolName)
        => EmptyArgHints.TryGetValue(toolName, out var hint) ? hint : EmptyArgHintDefault;

    public static string GetEmptyArgHintAscii(string toolName)
        => EmptyArgHintsAscii.TryGetValue(toolName, out var hint) ? hint : EmptyArgHintDefaultAscii;

    public AiChatToolOrchestrator(IMemoryCache cache, IMcpToolExecutor mcpToolExecutor, ILogger<AiChatService> logger)
    {
        _cache = cache;
        _mcpToolExecutor = mcpToolExecutor;
        _logger = logger;
    }

    // ── Agent Tool Registration & Cache ──────────────────────────────────────

    public async Task RegisterAgentToolsAsync(Guid agentId, Guid siteId,
        List<AgentToolRegistration> tools, CancellationToken ct = default)
    {
        var llmTools = tools.Select(t =>
        {
            object schema;
            try
            {
                schema = JsonSerializer.Deserialize<object>(t.ParametersSchemaJson)!;
                schema = EnrichAgentToolSchema(t.Name, t.ParametersSchemaJson, schema);
                ValidateAgentToolSchema(t.Name, t.ParametersSchemaJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[AgentTools] Schema inválido para tool '{ToolName}' do AgentId={AgentId}: {Error}",
                    t.Name, agentId, ex.Message);
                schema = new { type = "object", properties = new { } };
            }

            var description = EnrichAgentToolDescription(t.Name, t.Description);
            return new LlmTool(t.Name, description, schema);
        }).ToList();

        var cacheKey = $"agent_tools_{agentId}";
        _cache.Set(cacheKey, llmTools, AiChatConstants.AgentToolsCacheTtl);
        _logger.LogInformation("[AgentTools] {Count} tools registradas para AgentId={AgentId}", llmTools.Count, agentId);
        await Task.CompletedTask;
    }

    public List<LlmTool>? GetCachedAgentTools(Guid agentId)
    {
        var cacheKey = $"agent_tools_{agentId}";
        return _cache.TryGetValue(cacheKey, out List<LlmTool>? tools) ? tools : null;
    }

    // ── Schema Enrichment ────────────────────────────────────────────────────

    public static object EnrichAgentToolSchema(string toolName, string rawSchemaJson, object parsedSchema)
    {
        if (toolName is not ("search_packages" or "install_package" or "ask_user" or "create_ticket"))
            return parsedSchema;

        try
        {
            using var doc = JsonDocument.Parse(rawSchemaJson);
            var root = doc.RootElement;

            var hasRequired = root.TryGetProperty("required", out var requiredEl);
            var requiredList = hasRequired && requiredEl.ValueKind == JsonValueKind.Array
                ? requiredEl.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var missingRequired = toolName switch
            {
                "search_packages" => !requiredList.Contains("query"),
                "install_package" => !requiredList.Contains("id"),
                "ask_user" => !requiredList.Contains("question"),
                "create_ticket" => !requiredList.Contains("title") || !requiredList.Contains("description"),
                _ => false
            };

            if (!missingRequired) return parsedSchema;

            var enrichedJson = new Dictionary<string, object> { ["type"] = "object" };

            if (root.TryGetProperty("properties", out var props))
                enrichedJson["properties"] = JsonSerializer.Deserialize<object>(props.GetRawText())!;

            if (root.TryGetProperty("additionalProperties", out var ap))
                enrichedJson["additionalProperties"] = ap.ValueKind == JsonValueKind.False ? false : true;

            var newRequired = new List<string>(requiredList);
            var toAdd = toolName switch
            {
                "search_packages" => new[] { "query" },
                "install_package" => new[] { "id" },
                "ask_user" => new[] { "question" },
                "create_ticket" => new[] { "title", "description", "category", "priority" },
                _ => Array.Empty<string>()
            };
            foreach (var r in toAdd)
                if (!newRequired.Contains(r, StringComparer.OrdinalIgnoreCase))
                    newRequired.Add(r);

            enrichedJson["required"] = newRequired;
            return enrichedJson;
        }
        catch { return parsedSchema; }
    }

    public void ValidateAgentToolSchema(string toolName, string rawSchemaJson)
    {
        if (toolName is not ("search_packages" or "install_package" or "ask_user" or "create_ticket"))
            return;

        try
        {
            using var doc = JsonDocument.Parse(rawSchemaJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("required", out var requiredEl) || requiredEl.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("[AgentTools] Tool '{ToolName}' registrada sem campo 'required' no schema. Schema: {Schema}",
                    toolName, rawSchemaJson[..Math.Min(rawSchemaJson.Length, 200)]);
                return;
            }

            var requiredFields = requiredEl.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expected = toolName switch
            {
                "search_packages" => new[] { "query" },
                "install_package" => new[] { "id" },
                "ask_user" => new[] { "question" },
                "create_ticket" => new[] { "title", "description" },
                _ => Array.Empty<string>()
            };

            if (!expected.Any(e => requiredFields.Contains(e)))
            {
                _logger.LogWarning("[AgentTools] Tool '{ToolName}' sem campos obrigatórios relevantes. Esperado: {Expected}. Atual: {Actual}",
                    toolName, string.Join(", ", expected), string.Join(", ", requiredFields));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentTools] Erro ao validar schema da tool '{ToolName}'", toolName);
        }
    }

    // ── Description Enrichment ───────────────────────────────────────────────

    public static string EnrichAgentToolDescription(string toolName, string originalDescription)
    {
        var enrichment = toolName switch
        {
            "search_packages" => "Busca softwares e aplicativos disponíveis para instalação nos repositórios do RMM. Use APENAS quando o usuário solicitar instalar, atualizar ou buscar informações sobre programas (ex: 'Quero o Firefox', 'Tem Adobe?'). NÃO use esta ferramenta para buscar scripts de automação, configurações de rede, status de hardware ou logs de eventos. O parâmetro 'query' é OBRIGATÓRIO e deve conter o nome ou parte do nome do software (ex: 'chrome', 'office 365', 'adobe'). NUNCA envie query vazia. Se o usuário não especificou qual software, pergunte antes de chamar a ferramenta.",

            "install_package" => "Instala um programa no computador do usuário. PRÉ-REQUISITO: você DEVE ter obtido o 'id' exato da ferramenta 'search_packages' ANTES de chamar esta função. NUNCA invente ou adivinhe um ID — se ainda não tem o ID, chame 'search_packages' primeiro. NÃO use para desinstalar, atualizar ou reparar programas — apenas para instalação limpa. Aguarde a confirmação do usuário antes de instalar.",

            "ask_user" => "Faz uma pergunta ao usuário quando você precisar de mais informações para prosseguir. Use APENAS como último recurso, quando a informação necessária NÃO estiver disponível no histórico da conversa. NÃO use para perguntar o que o usuário já disse ou para iniciar conversa. O parâmetro 'question' é OBRIGATÓRIO e deve ser uma pergunta clara e contextualizada (ex: 'Qual versão do Office você precisa: 2019, 2021 ou Microsoft 365?'). NUNCA envie question vazia.",

            "create_ticket" => "Abre um chamado de suporte técnico para a equipe de TI. Use APENAS quando: (a) você não conseguiu resolver o problema com as ferramentas disponíveis, ou (b) o usuário solicitou explicitamente abrir um chamado. NÃO abra chamado sem antes tentar resolver o problema ou sem confirmar os dados com o usuário. Parâmetros obrigatórios: title (título resumido, ex: 'Instalação do Foxit Reader no DESKTOP-ABC'), description (detalhamento completo do problema, ações já tentadas e contexto), category (Software, Hardware, Rede, Impressora, Acesso/Senha, Outro) e priority (Baixa, Média, Alta — avalie pelo impacto e urgência). NUNCA envie parâmetros vazios — extraia do histórico da conversa.",

            _ => null
        };

        if (enrichment == null) return originalDescription;
        if (originalDescription.Length > 60)
            return enrichment + " — " + originalDescription;
        return enrichment;
    }

    public static string FormatAgentToolsDescription(List<LlmTool> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("O agente possui as seguintes ferramentas que podem ser usadas via function calling:");
        var hasCreateTicket = false;
        var hasSearchPackages = false;
        var hasInstallPackage = false;
        var hasAskUser = false;

        foreach (var tool in tools)
        {
            var desc = tool.Description;
            var firstLine = desc.Split('\n', '\r')[0];
            if (firstLine.Length > 120) firstLine = firstLine[..117] + "...";
            sb.AppendLine($"- `{tool.Name}`: {firstLine}");

            if (tool.Name == "create_ticket") hasCreateTicket = true;
            if (tool.Name == "search_packages") hasSearchPackages = true;
            if (tool.Name == "install_package") hasInstallPackage = true;
            if (tool.Name == "ask_user") hasAskUser = true;
        }
        sb.AppendLine();

        if (hasSearchPackages)
            sb.AppendLine(" `search_packages`: use APENAS para buscar softwares para instalação. NÃO use para scripts, configurações ou hardware. O parâmetro `query` é OBRIGATÓRIO — extraia o nome do programa da mensagem do usuário. Ex: 'Quero instalar o Adobe Acrobat' → query='Adobe Acrobat'. Query vazia causa falha.");

        if (hasInstallPackage)
        {
            sb.AppendLine(" `install_package`: REQUER que `search_packages` tenha sido chamada primeiro para obter o `id`. NUNCA invente um ID — se não tem o ID, execute `search_packages` antes. SEMPRE aguarde a confirmação do usuário antes de instalar.");
            sb.AppendLine("    NÃO use `install_package` para desinstalar, atualizar ou reparar — apenas para instalação limpa.");
        }

        if (hasAskUser)
            sb.AppendLine(" `ask_user`: use APENAS como último recurso, quando a informação NÃO está no histórico. O parâmetro `question` é OBRIGATÓRIO — SEMPRE preencha com uma pergunta clara e contextualizada.");

        if (hasCreateTicket)
            sb.AppendLine(" `create_ticket`: use APENAS quando esgotou as tentativas de solução OU o usuário pediu explicitamente. Preencha title, description, category e priority baseado no que foi discutido. Só execute APÓS confirmação do usuário.");

        sb.AppendLine();
        sb.AppendLine("Use estas ferramentas quando o usuário solicitar ações relacionadas. Sempre preencha todos os parâmetros obrigatórios com os valores fornecidos pelo usuário.");
        return sb.ToString();
    }

    // ── Error Wrapping ───────────────────────────────────────────────────────

    public static string WrapAgentToolError(string rawResult, string toolName)
    {
        if (rawResult.TrimStart().StartsWith("{")) return rawResult;

        var lower = rawResult.ToLowerInvariant();

        if (lower.Contains("nao pode ser vazio") || lower.Contains("não pode ser vazio")
            || lower.Contains("cannot be empty") || lower.Contains("is required")
            || lower.Contains("é obrigatório") || lower.Contains("e obrigatorio")
            || lower.Contains("parameter") && lower.Contains("missing"))
        {
            var hint = GetEmptyArgHint(toolName);

            return JsonSerializer.Serialize(new { error = rawResult.Trim(), tool = toolName, hint });
        }

        if (rawResult.Length < 100 && (lower.Contains("erro") || lower.Contains("error") || lower.Contains("falha") || lower.Contains("fail")))
        {
            return JsonSerializer.Serialize(new { error = rawResult.Trim(), tool = toolName, hint = "A ferramenta retornou erro. Analise a mensagem de erro e corrija o problema antes de tentar novamente." });
        }

        return rawResult;
    }

    // ── Argument Validation ──────────────────────────────────────────────────

    public static (bool IsValid, string? ErrorJson) ValidateAgentToolArguments(string toolName, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson == "{}" || argumentsJson == "null")
        {
            var errorMsg = toolName switch
            {
                "search_packages" => "query nao pode ser vazia",
                "create_ticket" => "title nao pode ser vazio",
                "ask_user" => "question nao pode ser vazia",
                "install_package" => "id nao pode ser vazio",
                _ => "parametros obrigatorios nao preenchidos"
            };

            var hint = GetEmptyArgHint(toolName);

            return (false, JsonSerializer.Serialize(new { error = errorMsg, tool = toolName, hint }));
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (false, JsonSerializer.Serialize(new { error = "argumentos devem ser um objeto JSON", tool = toolName, hint = "Forneça argumentos como um objeto JSON com os campos obrigatórios." }));

            var hasNonNull = root.EnumerateObject().Any(prop => prop.Value.ValueKind != JsonValueKind.Null);
            if (!hasNonNull)
                return (false, JsonSerializer.Serialize(new { error = "todos os parametros estao nulos", tool = toolName, hint = "Preencha os parâmetros obrigatórios com valores reais extraídos do histórico da conversa." }));

            var criticalProps = toolName switch
            {
                "search_packages" => new[] { "query" },
                "ask_user" => new[] { "question" },
                "create_ticket" => new[] { "title", "description" },
                "install_package" => new[] { "id" },
                _ => Array.Empty<string>()
            };

            foreach (var propName in criticalProps)
            {
                if (root.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(prop.GetString()))
                {
                    var errorMsg = propName switch
                    {
                        "query" => "query nao pode ser vazia — extraia o nome do programa da mensagem do usuario",
                        "question" => "question nao pode ser vazia — formule uma pergunta baseada no contexto",
                        "title" => "title nao pode ser vazio — extraia do historico da conversa",
                        "description" => "description nao pode ser vazio — extraia do historico da conversa",
                        _ => $"{propName} nao pode ser vazio"
                    };

                    var hint = GetEmptyArgHintAscii(toolName);

                    return (false, JsonSerializer.Serialize(new { error = errorMsg, tool = toolName, hint }));
                }
            }
        }
        catch (JsonException)
        {
            return (false, JsonSerializer.Serialize(new { error = "JSON invalido nos argumentos", tool = toolName, hint = "Corrija a formatação JSON dos argumentos." }));
        }

        return (true, null);
    }

    // ── XML Tool Call Fallback ───────────────────────────────────────────────

    public async Task<(string Content, int NextSeq)> ParseAndExecuteXmlToolCallsAsync(
        string content, List<LlmTool> availableTools,
        Guid scopeClientId, Guid scopeSiteId, Guid agentId,
        AIIntegrationSettings aiSettings, Guid? departmentId,
        List<LlmMessage> llmMessages, List<AiChatMessage> toolMessagesToPersist,
        Guid sessionId, int nextSeq, string traceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content)) return (content, nextSeq);

        // Diagnóstico: detecta marcação DSML nativa do modelo no output.
        // Não executa (não há como mapear invokes DSML para tools com
        // segurança), mas registra para monitorar a frequência do problema.
        if (DsmlDiagnosticRegex.IsMatch(content))
        {
            _logger.LogWarning("[{TraceId}] Marcação DSML nativa detectada no output do LLM (tool calls emitidas como texto). " +
                "Verifique o System Prompt / modelo — o ideal é que o modelo use function call nativa. " +
                "O conteúdo será sanitizado pelo AiChatLeakSanitizer.", traceId);
        }

        var matches = XmlToolCallRegex.Matches(content);
        if (matches.Count == 0) return (content, nextSeq);

        var knownToolNames = availableTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = content;
        var executedCount = 0;

        foreach (Match match in matches)
        {
            var rawToolName = match.Groups[1].Value;
            var argsJson = match.Groups[2].Value;

            var toolName = XmlToolAliases.TryGetValue(rawToolName, out var resolved) ? resolved : rawToolName;

            if (!knownToolNames.Contains(toolName))
            {
                _logger.LogDebug("[{TraceId}] XML tool call ignorada (tool desconhecida): {ToolName}", traceId, toolName);
                continue;
            }

            _logger.LogInformation("[{TraceId}] XML tool call detectada: {ToolName} args={Args}", traceId, toolName, argsJson);

            try
            {
                var toolResult = await _mcpToolExecutor.ExecuteAsync(toolName, argsJson, scopeClientId, scopeSiteId, agentId, aiSettings, null, departmentId, sessionId, ct);
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

                _logger.LogDebug("[{TraceId}] XML tool '{ToolName}' executada com sucesso", traceId, toolName);
                executedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{TraceId}] XML tool '{ToolName}' falhou", traceId, toolName);
            }

            result = result.Replace(match.Value, string.Empty);
        }

        if (executedCount > 0)
            _logger.LogInformation("[{TraceId}] {Count} XML tool(s) executadas e removidas do output", traceId, executedCount);

        return (result, nextSeq);
    }

    // ── History Reconstruction ───────────────────────────────────────────────

    public static List<LlmAssistantToolCall>? ParseToolCallsFromJson(string? toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(toolCallsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return null;

            var result = new List<LlmAssistantToolCall>();
            foreach (var item in root.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString()! : string.Empty;
                var name = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString()! : string.Empty;
                var args = item.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.String ? argsProp.GetString()! : "{}";
                result.Add(new LlmAssistantToolCall(id, name, args));
            }
            return result.Count > 0 ? result : null;
        }
        catch { return null; }
    }

    public static List<LlmMessage> BuildLlmMessagesFromHistory(List<AiChatMessage> history)
    {
        return history.OrderBy(m => m.SequenceNumber)
            .Select(m => new LlmMessage(m.Role, m.Content, m.ToolCallId, m.ToolName, ParseToolCallsFromJson(m.ToolCallsJson)))
            .ToList();
    }
}

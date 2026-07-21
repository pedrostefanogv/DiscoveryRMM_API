using System.Collections.Concurrent;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AiChatAgentToolRegistry
{
    private readonly ILogger<AiChatAgentToolRegistry> _logger;
    private static readonly ConcurrentDictionary<Guid, List<LlmTool>> _cache = new();
    private static readonly ConcurrentDictionary<Guid, DateTime> _cacheExpiry = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public AiChatAgentToolRegistry(ILogger<AiChatAgentToolRegistry> logger) { _logger = logger; }
    public TimeSpan Ttl => CacheTtl;

    public Task RegisterAsync(Guid agentId, List<AgentToolRegistration> tools, CancellationToken ct = default)
    {
        var llmTools = tools.Select(t =>
        {
            object schema;
            try { schema = JsonSerializer.Deserialize<object>(t.ParametersSchemaJson)!; }
            catch { schema = new { type = "object", properties = new { } }; }
            return new LlmTool(t.Name, t.Description, schema);
        }).ToList();

        _cache[agentId] = llmTools;
        _cacheExpiry[agentId] = DateTime.UtcNow.Add(CacheTtl);
        _logger.LogInformation("[AgentTools] {Count} tools registradas para AgentId={AgentId}", llmTools.Count, agentId);
        return Task.CompletedTask;
    }

    public List<LlmTool>? GetCachedTools(Guid agentId)
    {
        if (_cacheExpiry.TryGetValue(agentId, out var expiry) && expiry > DateTime.UtcNow)
            return _cache.TryGetValue(agentId, out var tools) ? tools : null;
        return null;
    }

    public static string FormatToolsDescription(List<LlmTool> tools)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("O agente possui as seguintes ferramentas que podem ser usadas via function calling:");
        foreach (var tool in tools)
        {
            var desc = tool.Description;
            var firstLine = desc.Split('\n', '\r')[0];
            if (firstLine.Length > 120) firstLine = firstLine[..117] + "...";
            sb.AppendLine(string.Concat("- `", tool.Name, "`: ", firstLine));
        }
        sb.AppendLine();
        sb.AppendLine("Use estas ferramentas quando o usuario solicitar acoes relacionadas. Sempre preencha todos os parametros obrigatorios com os valores fornecidos pelo usuario.");
        return sb.ToString();
    }

    public static string WrapAgentToolError(string rawResult, string toolName)
    {
        if (rawResult.TrimStart().StartsWith("{")) return rawResult;
        var lower = rawResult.ToLowerInvariant();

        if (lower.Contains("nao pode ser vazio") || lower.Contains("n\xE3o pode ser vazio")
            || lower.Contains("cannot be empty") || lower.Contains("is required")
            || lower.Contains("\xE9 obrigat\xF3rio") || lower.Contains("e obrigatorio")
            || (lower.Contains("parameter") && lower.Contains("missing")))
        {
            var hint = toolName switch
            {
                "search_packages" => "O parametro 'query' estava vazio. Extraia do historico da conversa o nome do programa.",
                "ask_user" => "O parametro 'question' estava vazio. Extraia da conversa o que voce precisa perguntar ao usuario.",
                "create_ticket" => "Parametros obrigatorios do chamado estavam vazios. Extraia do historico: titulo, descricao, categoria e prioridade.",
                "install_package" => "O parametro 'packageId' ou 'packageName' estava vazio. Extraia do historico o nome/id do programa.",
                _ => "O LLM enviou parametros vazios ou ausentes. Extraia os valores corretos da conversa."
            };
            return JsonSerializer.Serialize(new { error = rawResult.Trim(), tool = toolName, hint });
        }

        if (rawResult.Length < 100 && (lower.Contains("erro") || lower.Contains("error") || lower.Contains("falha") || lower.Contains("fail")))
        {
            return JsonSerializer.Serialize(new { error = rawResult.Trim(), tool = toolName, hint = "A ferramenta retornou erro. Analise a mensagem de erro e corrija o problema antes de tentar novamente." });
        }

        return rawResult;
    }

    public static HashSet<string> GetAgentToolNames(List<LlmTool>? tools)
        => new(tools?.Select(t => t.Name) ?? [], StringComparer.OrdinalIgnoreCase);
}

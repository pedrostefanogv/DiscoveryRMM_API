using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AiChatXmlToolParser
{
    private readonly IMcpToolExecutor _mcpToolExecutor;
    private readonly ILogger<AiChatXmlToolParser> _logger;

    private static readonly Regex XmlToolCallRegex = new(
        "<(\\w+)>\\s*(\\{[^}]*\\})\\s*</\\w+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
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

    public AiChatXmlToolParser(IMcpToolExecutor mcpToolExecutor, ILogger<AiChatXmlToolParser> logger)
    {
        _mcpToolExecutor = mcpToolExecutor;
        _logger = logger;
    }

    public async Task<(string Content, int NextSeq)> ParseAndExecuteAsync(
        string content, List<LlmTool> availableTools,
        Guid scopeClientId, Guid scopeSiteId, Guid agentId,
        AIIntegrationSettings aiSettings, Guid? departmentId,
        List<LlmMessage> llmMessages, List<AiChatMessage> toolMessagesToPersist,
        Guid sessionId, int nextSeq, string traceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content)) return (content, nextSeq);
        var matches = XmlToolCallRegex.Matches(content);
        if (matches.Count == 0) return (content, nextSeq);

        var knownToolNames = availableTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = content;
        var executedCount = 0;

        foreach (Match match in matches)
        {
            var rawToolName = match.Groups[1].Value;
            var argsJson = match.Groups[2].Value;
            var toolName = Aliases.TryGetValue(rawToolName, out var resolved) ? resolved : rawToolName;

            if (!knownToolNames.Contains(toolName))
            {
                _logger.LogDebug("[{TraceId}] XML tool call ignorada (tool desconhecida): {ToolName}", traceId, toolName);
                continue;
            }

            _logger.LogInformation("[{TraceId}] XML tool call detectada: {ToolName} args={Args}", traceId, toolName, argsJson);

            try
            {
                var toolResult = await _mcpToolExecutor.ExecuteAsync(toolName, argsJson, scopeClientId, scopeSiteId, agentId, aiSettings, null, departmentId, sessionId, ct);
                var toolCallId = string.Concat("xml_", Guid.NewGuid().ToString("N"));

                llmMessages.Add(new LlmMessage("assistant", string.Empty));
                llmMessages.Add(new LlmMessage("tool", toolResult, toolCallId, toolName));

                toolMessagesToPersist.Add(new AiChatMessage
                {
                    Id = Guid.NewGuid(), SessionId = sessionId, SequenceNumber = nextSeq++,
                    Role = "tool", Content = toolResult, ToolCallId = toolCallId,
                    ToolName = toolName, CreatedAt = DateTime.UtcNow, TraceId = traceId
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

    public static string ExtractKbQuery(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("query", out var qProp) && qProp.ValueKind == JsonValueKind.String)
                return qProp.GetString() ?? string.Empty;
        }
        catch { }
        return argumentsJson;
    }
}

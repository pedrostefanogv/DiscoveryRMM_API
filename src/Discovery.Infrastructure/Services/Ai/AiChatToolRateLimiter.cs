using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AiChatToolRateLimiter
{
    /// <summary>
    /// B16: número de rejeições consecutivas de argumentos de uma tool do agent
    /// antes de encerrar o loop de tools e sintetizar uma resposta sem tools.
    /// </summary>
    internal const int MaxConsecutiveAgentToolArgErrors = 3;

    private readonly IMcpToolGovernance _governance;
    private readonly ILogger<AiChatService> _logger;

    public AiChatToolRateLimiter(
        IMcpToolGovernance governance,
        ILogger<AiChatService> logger)
    {
        _governance = governance;
        _logger = logger;
    }

    /// <summary>
    /// Mapa tool→timeout (segundos) das tools do agente habilitadas, enviado ao
    /// agente no evento round_end para que ele aplique o timeout da política na
    /// execução local (o servidor não executa tools do agente).
    /// </summary>
    internal static Dictionary<string, int> BuildToolTimeouts(
        IReadOnlyList<McpToolPolicy> policies, HashSet<string> agentToolNames)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in policies)
        {
            if (!agentToolNames.Contains(p.ToolName) || !p.IsEnabled || p.TimeoutSeconds <= 0)
                continue;
            map[p.ToolName] = p.TimeoutSeconds;
        }
        return map;
    }

    /// <summary>
    /// Consome o rate limit da tool do agente. Retorna a mensagem de erro JSON a
    /// devolver ao LLM quando o limite foi excedido, ou null quando permitido.
    /// Tool sem política cadastrada não tem limite (comportamento herdado).
    ///
    /// O limite aplicado é o EFETIVO: o MENOR entre o limite da política da tool e
    /// o teto global herdável das configurações (servidor → cliente → site).
    /// </summary>
    internal async Task<string?> CheckAgentToolRateLimitAsync(
        string toolName, McpToolScope scope, IReadOnlyDictionary<string, McpToolPolicy> policies)
    {
        if (!policies.TryGetValue(toolName, out var policy))
            return null;

        int effectiveMax;
        try
        {
            effectiveMax = await _governance.ResolveMaxCallsPerMinuteAsync(scope, policy.MaxCallsPerMinute);
        }
        catch (Exception ex)
        {
            // Fail-open: indisponibilidade da configuração não pode derrubar o chat;
            // o limite da tool continua valendo.
            _logger.LogWarning(ex, "[MCP] Falha ao resolver o teto global de tools MCP; usando o limite da tool {ToolName}", toolName);
            effectiveMax = policy.MaxCallsPerMinute;
        }

        if (await _governance.TryConsumeRateLimitAsync(toolName, scope, effectiveMax))
            return null;

        _logger.LogWarning("[MCP] Rate limit excedido para tool do agente {ToolName} (max efetivo {Max}/min, limite da tool {PolicyMax}/min, escopo {Level})",
            toolName, effectiveMax, policy.MaxCallsPerMinute, scope.Level);

        return JsonSerializer.Serialize(new
        {
            error = $"Rate limit excedido para '{toolName}'. Máximo: {effectiveMax} chamadas por minuto. Aguarde antes de tentar novamente."
        });
    }
}

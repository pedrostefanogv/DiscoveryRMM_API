using System.Text.Json;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Handlers adicionais de MCP tools registrados no startup.
/// </summary>
public static class McpToolHandlers
{
    /// <summary>
    /// Registra todos os handlers nativos (não dependentes de escopos externos).
    /// Handlers como knowledge_search e filesystem.read_file são registrados diretamente no McpToolExecutor.
    /// </summary>
    public static void RegisterBuiltInHandlers(IMcpToolExecutor executor)
    {
        // time.current: retorna data/hora atual UTC e local
        executor.RegisterHandler("time.current", async ctx =>
        {
            var now = DateTimeOffset.UtcNow;
            var local = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, "E. South America Standard Time");
            return JsonSerializer.Serialize(new
            {
                utc = now.ToString("O"),
                local = local.ToString("O"),
                timezone = "America/Sao_Paulo",
                unix_timestamp = now.ToUnixTimeSeconds()
            });
        });

        // sequential_thinking: estrutura de raciocínio multi-step
        executor.RegisterHandler("sequential_thinking", async ctx =>
        {
            var thought = ctx.Arguments.RootElement.TryGetProperty("thought", out var tProp)
                ? tProp.GetString() ?? ""
                : "";
            var step = ctx.Arguments.RootElement.TryGetProperty("step", out var sProp)
                ? sProp.GetInt32()
                : 0;

            // Esta tool é puramente estrutural — o LLM a usa para organizar pensamentos
            return JsonSerializer.Serialize(new
            {
                acknowledged = true,
                step,
                message = $"Pensamento registrado no passo {step}. Continue a análise."
            });
        });

        // memory.search: busca na "memória" — atualmente usa apenas contexto da conversa
        executor.RegisterHandler("memory.search", async ctx =>
        {
            var query = ctx.Arguments.RootElement.TryGetProperty("query", out var qProp)
                ? qProp.GetString() ?? ""
                : "";

            // Por enquanto, retorna que não há memória persistente
            // Futuro: integrar com vector store de memórias por agent
            return JsonSerializer.Serialize(new
            {
                results = Array.Empty<object>(),
                note = "Memória persistente ainda não configurada para este agent. Use o histórico da conversa."
            });
        });
    }
}

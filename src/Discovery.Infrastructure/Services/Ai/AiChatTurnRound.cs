using Microsoft.Extensions.Caching.Memory;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Contador de rounds por TURNO (não por request HTTP) do agent loop.
///
/// Por que existe: tool chains do agent/MCP são encerradas com round_end e
/// retomadas em OUTRO request (StreamMultiRoundAsync). O contador local do
/// request (toolIterations) reiniciava a cada request, então o heartbeat
/// "loop_progress" reportava sempre LoopRound=1 e o orçamento configurado
/// (MaxToolCallIterations) nunca era aplicado no caminho delegado ao agent —
/// quem limitava era o próprio agent (20 rounds/10min). Guardar o round na
/// cache compartilhada pela sessão mantém a contagem ao longo do turno.
/// </summary>
internal static class AiChatTurnRound
{
    /// <summary>Prefixo da chave de cache por sessão (mesmo padrão de pending_round:).</summary>
    public const string KeyPrefix = "turn_round:";

    /// <summary>
    /// Nome do toolResult sintético que representa um clique/input em uma surface
    /// A2UI (ver AiChatStreamingOrchestrator: a ação chega como tool result com
    /// Message nulo, porque o provedor exige ToolResults para o fluxo
    /// multi-round).
    /// </summary>
    public const string A2uiActionToolName = "a2ui_action";

    public static string BuildKey(Guid sessionId) => $"{KeyPrefix}{sessionId}";

    /// <summary>
    /// Decide se um request com ToolResults é CONTINUAÇÃO da tool chain do turno
    /// anterior (round + 1) ou um TURNO NOVO (round 1).
    ///
    /// Correção 2026-10-08 (caso YogaDNS): um clique em surface A2UI chega com
    /// ToolResults, então era contado como continuação do turno anterior. Cada
    /// clique somava rounds ao contador e, com MaxToolCallIterations baixo
    /// (homologação = 3), o clique seguinte estourava o orçamento (4/3): o
    /// tool_call que o LLM emitiu era ABORTADO ("sintetizando resposta sem
    /// tools") e o programa nunca era atualizado — o usuário via "não consegui
    /// executar" sem nenhum erro real.
    ///
    /// Um clique A2UI é, para o usuário, uma ação NOVA: só conta como
    /// continuação quando há tool results reais (qualquer result que não seja
    /// exclusivamente a sentinela a2ui_action).
    /// </summary>
    public static bool ResolveContinuesTurn(IEnumerable<string>? toolResultNames)
    {
        var names = toolResultNames?.ToList() ?? [];
        if (names.Count == 0) return false;
        return !names.All(IsA2uiAction);
    }

    private static bool IsA2uiAction(string name) =>
        string.Equals(name, A2uiActionToolName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve o round atual do turno e renova o TTL da chave.
    /// hasToolResults=true indica continuação de um turno já iniciado (round
    /// anterior + 1); false indica primeiro round de um turno novo (= 1).
    /// Ausência na cache em uma continuação conta como 0 → round 1 (ex.: TTL
    /// expirou no meio da chain); reiniciar é mais seguro que estourar de uma vez.
    /// </summary>
    public static int Resolve(IMemoryCache cache, Guid sessionId, bool hasToolResults)
    {
        var key = BuildKey(sessionId);
        var round = hasToolResults && cache.TryGetValue(key, out int previous) ? previous + 1 : 1;
        // Sempre Set: sem renovar, o TTL expiraria no meio de uma tool chain longa.
        cache.Set(key, round, AiChatConstants.TurnRoundTtl);
        return round;
    }

    /// <summary>
    /// Orçamento esgotado quando o round do turno ULTRAPASSA o máximo configurado.
    ///
    /// Correção 2026-10-08 (off-by-one): antes era <c>turnRound >= maxIterations</c>
    /// e o tool_call só era emitido com <c>!IsBudgetExhausted</c>, então o valor
    /// configurado valia como N-1 execuções (com 3 → 2; com 1 → nenhuma). Agora
    /// <c>maxIterations = 3</c> significa até 3 rounds de ferramenta por turno, que
    /// é o que o campo da tela promete. O mínimo configurável é 3
    /// (<see cref="AiChatConstants.MinToolCallIterations"/>).
    /// </summary>
    public static bool IsBudgetExhausted(int turnRound, int maxIterations)
        => turnRound > maxIterations;
}

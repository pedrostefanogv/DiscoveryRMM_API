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

    public static string BuildKey(Guid sessionId) => $"{KeyPrefix}{sessionId}";

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
    /// Orçamento esgotado quando o round do turno alcança o máximo configurado.
    /// Usado no caminho delegado, onde antes o yield break ignorava inteiramente
    /// o MaxToolCallIterations definido pelo administrador.
    /// </summary>
    public static bool IsBudgetExhausted(int turnRound, int maxIterations)
        => turnRound >= maxIterations;
}

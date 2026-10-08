using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Discovery.Tests;

/// <summary>
/// Testes do contador de round por TURNO (AiChatTurnRound).
/// Contexto: tool chains do agent/MCP retomam em outro request; o contador
/// local (toolIterations) zerava e o heartbeat "loop_progress" reportava sempre
/// round 1, e o orçamento MaxToolCallIterations era ignorado no caminho
/// delegado ao agent. Ver AiChatStreamingOrchestrator.
/// </summary>
public class AiChatTurnRoundTests
{
    // ── Resolve: turno novo x continuação ────────────────────────────────────

    [Test]
    public void Resolve_NewTurn_Returns1AndStoresInCache()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessionId = Guid.NewGuid();

        var round = AiChatTurnRound.Resolve(cache, sessionId, hasToolResults: false);

        Assert.That(round, Is.EqualTo(1));
        // Sempre Set: a chave precisa existir para a próxima continuação somar.
        Assert.That(cache.TryGetValue(AiChatTurnRound.BuildKey(sessionId), out int stored), Is.True);
        Assert.That(stored, Is.EqualTo(1));
    }

    [Test]
    public void Resolve_Continuation_IncrementsRounds()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessionId = Guid.NewGuid();

        Assert.That(AiChatTurnRound.Resolve(cache, sessionId, hasToolResults: false), Is.EqualTo(1));
        Assert.That(AiChatTurnRound.Resolve(cache, sessionId, hasToolResults: true), Is.EqualTo(2));
        Assert.That(AiChatTurnRound.Resolve(cache, sessionId, hasToolResults: true), Is.EqualTo(3));
    }

    [Test]
    public void Resolve_ContinuationWithoutCachedRound_Returns1()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());

        // Cache expirou/ausente: reinicia em 1 em vez de estourar o orçamento.
        Assert.That(AiChatTurnRound.Resolve(cache, Guid.NewGuid(), hasToolResults: true), Is.EqualTo(1));
    }

    [Test]
    public void Resolve_NewUserTurnAfterDelegation_ResetsTo1()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessionId = Guid.NewGuid();

        AiChatTurnRound.Resolve(cache, sessionId, hasToolResults: false); // turno A, round 1
        AiChatTurnRound.Resolve(cache, sessionId, hasToolResults: true);  // turno A, round 2

        // Mensagem nova do usuário (sem ToolResults) = novo turno.
        Assert.That(AiChatTurnRound.Resolve(cache, sessionId, hasToolResults: false), Is.EqualTo(1));
        Assert.That(cache.TryGetValue(AiChatTurnRound.BuildKey(sessionId), out int stored), Is.True);
        Assert.That(stored, Is.EqualTo(1));
    }

    [Test]
    public void Resolve_ContinuesRenewingCacheAfterEachCall()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessionId = Guid.NewGuid();

        AiChatTurnRound.Resolve(cache, sessionId, hasToolResults: false);
        AiChatTurnRound.Resolve(cache, sessionId, hasToolResults: true);

        Assert.That(cache.TryGetValue(AiChatTurnRound.BuildKey(sessionId), out int stored), Is.True);
        Assert.That(stored, Is.EqualTo(2));
    }

    // ── Chave / prefixo (não colide com pending_round) ───────────────────────

    [Test]
    public void BuildKey_UsesTurnRoundPrefixAndSessionId()
    {
        var sessionId = Guid.NewGuid();

        Assert.That(AiChatTurnRound.KeyPrefix, Is.EqualTo("turn_round:"));
        Assert.That(AiChatTurnRound.BuildKey(sessionId), Is.EqualTo($"turn_round:{sessionId}"));
        Assert.That(AiChatTurnRound.BuildKey(sessionId), Does.Not.StartWith("pending_round:"));
    }

    // ── Orçamento esgotado ───────────────────────────────────────────────────

    [Test]
    public void IsBudgetExhausted_WhenRoundReachesMax_IsTrue()
    {
        Assert.That(AiChatTurnRound.IsBudgetExhausted(3, 3), Is.True);
    }

    [Test]
    public void IsBudgetExhausted_WhenRoundBelowMax_IsFalse()
    {
        Assert.That(AiChatTurnRound.IsBudgetExhausted(2, 3), Is.False);
    }

    [Test]
    public void IsBudgetExhausted_WhenRoundAboveMax_IsTrue()
    {
        Assert.That(AiChatTurnRound.IsBudgetExhausted(4, 3), Is.True);
    }

    [Test]
    public void IsBudgetExhausted_WithSingleRoundBudget_IsTrueOnFirstRound()
    {
        // maxIterations == 1: nenhuma delegação é permitida já no 1º round.
        Assert.That(AiChatTurnRound.IsBudgetExhausted(1, 1), Is.True);
        Assert.That(AiChatTurnRound.IsBudgetExhausted(1, 2), Is.False);
    }

    // ── TTL ──────────────────────────────────────────────────────────────────

    [Test]
    public void Constants_TurnRoundTtl_CoversWholeAgentTurn()
    {
        // O TTL tem de cobrir o turno INTEIRO (o agente executa as tools entre
        // dois requests e uma cadeia real passa de 2min). O teto do loop do
        // agente é 10min; o watchdog de round pendente segue com 120s.
        Assert.That(AiChatConstants.TurnRoundTtl, Is.GreaterThanOrEqualTo(TimeSpan.FromMinutes(10)));
        Assert.That(AiChatConstants.TurnRoundTtl, Is.GreaterThan(AiChatConstants.PendingRoundTtl));
    }
}

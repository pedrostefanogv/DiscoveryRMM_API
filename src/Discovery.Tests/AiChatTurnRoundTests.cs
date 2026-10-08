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
    public void IsBudgetExhausted_AtTheConfiguredLimit_StillAllowsExecution()
    {
        // Correção 2026-10-08 (off-by-one): com 3 configurado o round 3 ainda
        // delega — o valor passou a valer como N execuções (antes valia N-1).
        Assert.That(AiChatTurnRound.IsBudgetExhausted(3, 3), Is.False);
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
    public void IsBudgetExhausted_WithSingleRoundBudget_AllowsFirstRound()
    {
        // Com 1 configurado (abaixo do mínimo da tela, mas possível via banco) o
        // round 1 executa; o 2º é que estoura — antes o 1 já estourava e
        // "1 round" significava NENHUMA ferramenta.
        Assert.That(AiChatTurnRound.IsBudgetExhausted(1, 1), Is.False);
        Assert.That(AiChatTurnRound.IsBudgetExhausted(2, 1), Is.True);
        Assert.That(AiChatTurnRound.IsBudgetExhausted(1, 2), Is.False);
    }

    // ── Clique em surface A2UI = TURNO NOVO (regressão YogaDNS 2026-10-08) ───

    [Test]
    public void ResolveContinuesTurn_EmptyOrNull_IsNewTurn()
    {
        Assert.That(AiChatTurnRound.ResolveContinuesTurn(null), Is.False);
        Assert.That(AiChatTurnRound.ResolveContinuesTurn([]), Is.False);
    }

    [Test]
    public void ResolveContinuesTurn_A2uiActionOnly_IsNewTurn()
    {
        Assert.That(AiChatTurnRound.ResolveContinuesTurn(["a2ui_action"]), Is.False);
        // O servidor compara nomes de tool ignorando caixa (validação B1).
        Assert.That(AiChatTurnRound.ResolveContinuesTurn(["A2UI_ACTION"]), Is.False);
    }

    [Test]
    public void ResolveContinuesTurn_RealToolResults_IsContinuation()
    {
        Assert.That(AiChatTurnRound.ResolveContinuesTurn(["upgrade_package"]), Is.True);
        // Resultado real + sentinela: o que importa é a tool chain real.
        Assert.That(AiChatTurnRound.ResolveContinuesTurn(["a2ui_action", "upgrade_package"]), Is.True);
    }

    [Test]
    public void Resolve_TwoA2uiClicksInARow_DoNotExhaustBudget()
    {
        // Cenário real de homologação (MaxToolCallIterations=3):
        //   1º clique: rounds 1 (tool_call) e 2 (resultado real)
        //   2º clique ANTES da correção: round 3/4 -> "Orçamento de rounds do
        //   turno esgotado (4/3); sintetizando resposta sem tools" e o
        //   upgrade_package clicado era abortado (YogaDNS não atualizou).
        // Com a correção cada clique reinicia em round 1: o orçamento vale POR
        // turno/clique, não acumula entre cliques.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessionId = Guid.NewGuid();
        const int maxIterations = 3;

        var click1 = AiChatTurnRound.Resolve(cache, sessionId,
            AiChatTurnRound.ResolveContinuesTurn(["a2ui_action"]));
        Assert.That(click1, Is.EqualTo(1));

        var chain = AiChatTurnRound.Resolve(cache, sessionId,
            AiChatTurnRound.ResolveContinuesTurn(["upgrade_package"]));
        Assert.That(chain, Is.EqualTo(2));
        Assert.That(AiChatTurnRound.IsBudgetExhausted(chain, maxIterations), Is.False);

        var click2 = AiChatTurnRound.Resolve(cache, sessionId,
            AiChatTurnRound.ResolveContinuesTurn(["a2ui_action"]));
        Assert.That(click2, Is.EqualTo(1));
        Assert.That(AiChatTurnRound.IsBudgetExhausted(click2, maxIterations), Is.False);
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

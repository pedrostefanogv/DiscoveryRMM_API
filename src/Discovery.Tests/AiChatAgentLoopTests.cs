using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes do agent loop resiliente do chat IA:
/// - Resolução do orçamento de iterações (default 10, clamp 1-20)
/// - Notas de sistema compartilhadas (síntese forçada, KB esgotada, round expirado)
/// - Chunk de progresso do loop (loop_progress retrocompatível)
/// Contexto: o chat "travava" sem responder quando o orçamento de iterações
/// esgotava em silêncio ou a KB não retornava resultados — ver
/// docs_planejamento/AI_CHAT_AGENT_LOOP_PLAN.md.
/// </summary>
public class AiChatAgentLoopTests
{
    // ── ResolveMaxToolIterations ─────────────────────────────────────────────

    [Test]
    public void ResolveMaxToolIterations_WhenUnset_ReturnsDefault10()
    {
        var settings = new AIIntegrationSettings { MaxToolCallIterations = 0 };

        Assert.That(AiChatHelpers.ResolveMaxToolIterations(settings), Is.EqualTo(10));
    }

    [Test]
    public void ResolveMaxToolIterations_WhenNegative_ReturnsDefault10()
    {
        var settings = new AIIntegrationSettings { MaxToolCallIterations = -3 };

        Assert.That(AiChatHelpers.ResolveMaxToolIterations(settings), Is.EqualTo(10));
    }

    [Test]
    public void ResolveMaxToolIterations_WhenValidValue_ReturnsValue()
    {
        var settings = new AIIntegrationSettings { MaxToolCallIterations = 7 };

        Assert.That(AiChatHelpers.ResolveMaxToolIterations(settings), Is.EqualTo(7));
    }

    [Test]
    public void ResolveMaxToolIterations_WhenAboveLimit_ReturnsDefault()
    {
        var settings = new AIIntegrationSettings { MaxToolCallIterations = 50 };

        Assert.That(AiChatHelpers.ResolveMaxToolIterations(settings), Is.EqualTo(10));
    }

    [Test]
    public void ResolveMaxToolIterations_WhenAtLimit20_Returns20()
    {
        var settings = new AIIntegrationSettings { MaxToolCallIterations = 20 };

        Assert.That(AiChatHelpers.ResolveMaxToolIterations(settings), Is.EqualTo(20));
    }

    [Test]
    public void DefaultSetting_Is10()
    {
        var settings = new AIIntegrationSettings();

        Assert.That(settings.MaxToolCallIterations, Is.EqualTo(10));
    }

    // ── Notas de sistema ─────────────────────────────────────────────────────

    [Test]
    public void SynthesisBudgetNote_ForbidsFurtherToolCalls()
    {
        Assert.That(AiChatHelpers.SynthesisBudgetNote, Does.Contain("NÃO faça mais chamadas de ferramentas"));
        Assert.That(AiChatHelpers.SynthesisBudgetNote, Does.StartWith("[SISTEMA]"));
    }

    [Test]
    public void KbExhaustedNote_ForbidsFurtherKbSearches()
    {
        Assert.That(AiChatHelpers.KbExhaustedNote, Does.Contain("NÃO faça novas buscas"));
    }

    [Test]
    public void AgentRoundExpiredNote_InformsUserAboutFailure()
    {
        Assert.That(AiChatHelpers.AgentRoundExpiredNote, Does.Contain("expirou"));
        Assert.That(AiChatHelpers.AgentRoundExpiredNote, Does.Contain("sugira alternativas"));
    }

    [Test]
    public void EmptyContentNote_RequestsVisibleAnswer()
    {
        Assert.That(AiChatHelpers.EmptyContentNote, Does.Contain("resposta visível"));
    }

    // ── Constantes do loop ───────────────────────────────────────────────────

    [Test]
    public void Constants_DefaultMaxToolCallIterations_Is10()
    {
        Assert.That(AiChatConstants.DefaultMaxToolCallIterations, Is.EqualTo(10));
    }

    [Test]
    public void Constants_MaxToolCallIterationsLimit_Is20()
    {
        Assert.That(AiChatConstants.MaxToolCallIterationsLimit, Is.EqualTo(20));
    }

    [Test]
    public void Constants_MaxSynthesisRetries_IsAtLeast2()
    {
        Assert.That(AiChatConstants.MaxSynthesisRetries, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void Constants_PendingRoundTtl_IsAtLeast60Seconds()
    {
        Assert.That(AiChatConstants.PendingRoundTtl, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(60)));
    }

    // ── Chunk loop_progress (contrato retrocompatível) ──────────────────────

    [Test]
    public void StreamChunk_LoopProgress_CarriesRoundAndMaxRounds()
    {
        var chunk = new Discovery.Core.DTOs.AiChatStreamChunk(
            Type: "loop_progress", LoopRound: 3, LoopMaxRounds: 10);

        Assert.That(chunk.Type, Is.EqualTo("loop_progress"));
        Assert.That(chunk.LoopRound, Is.EqualTo(3));
        Assert.That(chunk.LoopMaxRounds, Is.EqualTo(10));
        Assert.That(chunk.Content, Is.Null);
        Assert.That(chunk.Error, Is.Null);
    }

    [Test]
    public void StreamChunk_TokenChunk_HasNullLoopFields_BackwardCompatible()
    {
        var chunk = new Discovery.Core.DTOs.AiChatStreamChunk(Type: "token", Content: "olá");

        Assert.That(chunk.LoopRound, Is.Null);
        Assert.That(chunk.LoopMaxRounds, Is.Null);
    }
}

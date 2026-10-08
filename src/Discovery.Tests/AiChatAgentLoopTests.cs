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
    public void ResolveMaxToolIterations_BelowMinimum_ReturnsDefault10()
    {
        // Mínimo configurável é 3 (tela). 1/2 gravados à mão caem no default —
        // nunca em "quase nenhuma ferramenta".
        Assert.That(AiChatHelpers.ResolveMaxToolIterations(new AIIntegrationSettings { MaxToolCallIterations = 1 }), Is.EqualTo(10));
        Assert.That(AiChatHelpers.ResolveMaxToolIterations(new AIIntegrationSettings { MaxToolCallIterations = 2 }), Is.EqualTo(10));
    }

    [Test]
    public void ResolveMaxToolIterations_AtMinimum_ReturnsValue()
    {
        Assert.That(AiChatHelpers.ResolveMaxToolIterations(new AIIntegrationSettings { MaxToolCallIterations = 3 }), Is.EqualTo(3));
    }

    [Test]
    public void Constants_MinToolCallIterations_Is3()
    {
        Assert.That(AiChatConstants.MinToolCallIterations, Is.EqualTo(3));
    }

    [Test]
    public void AgentBudgetContinuationFollowUpNote_AsksWithoutRepeating()
    {
        var note = AiChatHelpers.AgentBudgetContinuationFollowUpNote;

        Assert.That(note, Does.StartWith("[SISTEMA]"));
        Assert.That(note, Does.Contain("- Continuar"));
        Assert.That(note, Does.Contain("NÃO repita o que você já escreveu"));
        Assert.That(note, Does.Contain("NÃO solicite ferramentas"));
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
    public void KbExhaustedNote_ForbidsRepeatingContentSearches()
    {
        Assert.That(AiChatHelpers.KbExhaustedNote, Does.Contain("NÃO repita buscas de conteúdo"));
    }

    [Test]
    public void KbExhaustedNote_PointsToKnowledgeListForCatalog()
    {
        Assert.That(AiChatHelpers.KbExhaustedNote, Does.Contain("knowledge_list"));
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

    // ── Renovação de orçamento com confirmação do usuário ───────────────────

    [Test]
    public void AgentBudgetContinuationNote_AsksUserAndForbidsToolCalls()
    {
        var note = AiChatHelpers.AgentBudgetContinuationNote;

        Assert.That(note, Does.StartWith("[SISTEMA]"));
        Assert.That(note, Does.Contain("NÃO execute nem solicite ferramentas agora"));
        Assert.That(note, Does.Contain("- Continuar"));
        Assert.That(note, Does.Contain("- Parar"));
        Assert.That(note, Does.Contain("retomada automaticamente no próximo turno"));
    }

    [Test]
    public void AgentToolBudgetAwaitingConsentResult_IsNotAnExecutionFailure()
    {
        var result = AiChatHelpers.AgentToolBudgetAwaitingConsentResult;

        Assert.That(result, Does.Contain("\"awaiting_user_consent\":true"));
        Assert.That(result, Does.Contain("NÃO executada"));
        // Não pode dizer "esgotado sem executar" como se fosse erro de execução:
        // o usuário não deve receber aviso de falha quando nada falhou.
        Assert.That(result, Does.Not.Contain("falhou"));
    }

    [Test]
    public void BudgetRenewalResumeNote_ListsPendingActionsAndAllowsRefusal()
    {
        var note = AiChatHelpers.BuildBudgetRenewalResumeNote(
            new[] { "upgrade_package {\"id\":\"Initex.YogaDNS\"}", "list_print_jobs {}" });

        Assert.That(note, Does.StartWith("[SISTEMA]"));
        Assert.That(note, Does.Contain("upgrade_package"));
        Assert.That(note, Does.Contain("Initex.YogaDNS"));
        Assert.That(note, Does.Contain("retome AGORA exatamente essas ações"));
        Assert.That(note, Does.Contain("o orçamento deste turno está renovado"));
        Assert.That(note, Does.Contain("Se a resposta recusar"));
        // Mensagem sobre outro assunto não pode ser sequestrada pelas pendências.
        Assert.That(note, Does.Contain("OUTRO assunto"));
    }

    [Test]
    public void BudgetRenewalResumeNote_WithoutPendingActions_DoesNotBreak()
    {
        Assert.That(AiChatHelpers.BuildBudgetRenewalResumeNote(null), Does.Contain("(não identificadas)"));
        Assert.That(AiChatHelpers.BuildBudgetRenewalResumeNote(Array.Empty<string>()), Does.Contain("(não identificadas)"));
    }

    [Test]
    public void BuildPendingActionSummary_FormatsTruncatesAndCaps()
    {
        var pending = new List<(string Name, string? ArgumentsJson)>
        {
            ("upgrade_package", "{\"id\":\"Initex.YogaDNS\"}"),
            ("", "sem nome — deve ser ignorado"),
            ("install_package", new string('x', 500)),
        };

        var summary = AiChatHelpers.BuildPendingActionSummary(pending);

        Assert.That(summary, Has.Count.EqualTo(2));
        Assert.That(summary[0], Is.EqualTo("upgrade_package {\"id\":\"Initex.YogaDNS\"}"));
        Assert.That(summary[1], Does.StartWith("install_package "));
        Assert.That(summary[1].Length, Is.LessThan(260));

        // Cap: a nota de sistema não pode virar dump de 50 ações.
        var many = Enumerable.Range(0, 50).Select(i => ($"tool_{i}", (string?)"{}")).ToList();
        Assert.That(AiChatHelpers.BuildPendingActionSummary(many),
            Has.Count.EqualTo(AiChatConstants.MaxBudgetRenewalPendingActions));
    }

    [Test]
    public void BudgetRenewalCacheKey_IsScopedBySessionAndExpires()
    {
        var sessionId = Guid.NewGuid();

        Assert.That(AiChatConstants.BudgetRenewalKey(sessionId),
            Is.EqualTo($"budget_renewal:{sessionId}"));
        Assert.That(AiChatConstants.BudgetRenewalKey(sessionId), Does.Not.StartWith("turn_round:"));
        // O usuário pode demorar para responder: TTL precisa cobrir isso.
        Assert.That(AiChatConstants.BudgetRenewalTtl, Is.GreaterThanOrEqualTo(TimeSpan.FromMinutes(30)));
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

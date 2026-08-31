using Discovery.Core.Entities;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes das otimizações de cost control, quick-reply e hash de cache RAG.
/// </summary>
public class AiChatOptimizationsTests
{
    // ── QuickReply ──────────────────────────────────────────────────────────

    [Test]
    public void QuickReply_ExactGreeting_MatchesEvenWithHistory()
    {
        // Otimização: saudação pura no meio da conversa também usa cache.
        var history = new List<AiChatMessage> { new() { Role = "user", Content = "pergunta anterior" } };

        var reply = AiChatQuickReply.TryGetReply("oi", history);

        Assert.That(reply, Is.Not.Null);
        Assert.That(reply, Does.Contain("Olá"));
    }

    [Test]
    public void QuickReply_PartialGreeting_DoesNotMatchWithHistory()
    {
        // Match parcial ("oi, tudo bem?") só na 1ª mensagem: no meio da
        // conversa pode ser resposta a uma pergunta, não saudação.
        var history = new List<AiChatMessage> { new() { Role = "user", Content = "pergunta anterior" } };

        var reply = AiChatQuickReply.TryGetReply("oi, tudo bem?", history);

        Assert.That(reply, Is.Null);
    }

    [Test]
    public void QuickReply_PartialGreeting_MatchesWithoutHistory()
    {
        // "oi, tudo bem?" tem 4 palavras — acima do limite de 3 do matcher
        // parcial. Usa uma saudação parcial de até 3 palavras.
        var reply = AiChatQuickReply.TryGetReply("oi, tudo bem", null);

        Assert.That(reply, Is.Not.Null);
    }

    [Test]
    public void QuickReply_SubstantiveMessage_DoesNotMatch()
    {
        // Mensagem com conteúdo real nunca faz match, com ou sem histórico.
        Assert.That(AiChatQuickReply.TryGetReply("meu computador está lento", null), Is.Null);
        Assert.That(AiChatQuickReply.TryGetReply("meu computador está lento",
            new List<AiChatMessage>()), Is.Null);
    }

    // ── ComputeMessageHash (cache RAG) ──────────────────────────────────────

    [Test]
    public void MessageHash_AccentInsensitive_SameHash()
    {
        // "não consigo imprimir" e "nao consigo imprimir" devem compartilhar cache.
        var h1 = AiChatSystemPromptBuilder.ComputeMessageHash("não consigo imprimir");
        var h2 = AiChatSystemPromptBuilder.ComputeMessageHash("nao consigo imprimir");

        Assert.That(h1, Is.EqualTo(h2));
    }

    [Test]
    public void MessageHash_PunctuationInsensitive_SameHash()
    {
        var h1 = AiChatSystemPromptBuilder.ComputeMessageHash("impressora não funciona!");
        var h2 = AiChatSystemPromptBuilder.ComputeMessageHash("impressora nao funciona");

        Assert.That(h1, Is.EqualTo(h2));
    }

    [Test]
    public void MessageHash_WhitespaceNormalized_SameHash()
    {
        var h1 = AiChatSystemPromptBuilder.ComputeMessageHash("rede    lenta");
        var h2 = AiChatSystemPromptBuilder.ComputeMessageHash("rede lenta");

        Assert.That(h1, Is.EqualTo(h2));
    }

    [Test]
    public void MessageHash_DifferentMessages_DifferentHash()
    {
        var h1 = AiChatSystemPromptBuilder.ComputeMessageHash("impressora não funciona");
        var h2 = AiChatSystemPromptBuilder.ComputeMessageHash("computador muito lento");

        Assert.That(h1, Is.Not.EqualTo(h2));
    }

    [Test]
    public void MessageHash_CaseInsensitive_SameHash()
    {
        var h1 = AiChatSystemPromptBuilder.ComputeMessageHash("Computador Lento");
        var h2 = AiChatSystemPromptBuilder.ComputeMessageHash("computador lento");

        Assert.That(h1, Is.EqualTo(h2));
    }
}

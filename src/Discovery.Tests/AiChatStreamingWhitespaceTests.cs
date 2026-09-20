using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressão do bug de whitespace no streaming do chat (produção, 19-20/09/2026):
/// o orquestrador descartava deltas do LLM contendo somente espaço/quebra de
/// linha (IsNullOrWhiteSpace), colando palavras ("últimas24h"), itens de lista
/// ("travando.2.") e o título à tabela ("agora| Processo |") na resposta
/// exibida ao usuário. A via sync — que não passa pelo filtro — saía limpa,
/// o que isolou a causa no streaming.
/// Contrato: token de texto é propagado com QUALQUER conteúdo não vazio,
/// incluindo whitespace puro — é ele quem separa palavras e linhas markdown.
/// </summary>
public class AiChatStreamingWhitespaceTests
{
    [Test]
    public void TextToken_SpaceOnly_IsPropagated()
    {
        var evt = new LlmStreamEvent(Type: "token", Content: " ");

        Assert.That(AiChatStreamingOrchestrator.IsTextToken(evt), Is.True);
    }

    [Test]
    public void TextToken_NewLineOnly_IsPropagated()
    {
        var evt = new LlmStreamEvent(Type: "token", Content: "\n");

        Assert.That(AiChatStreamingOrchestrator.IsTextToken(evt), Is.True);
    }

    [Test]
    public void TextToken_BlankLinesBetweenBlocks_ArePropagated()
    {
        // "\n\n" separa parágrafos/blocos markdown — era exatamente o que
        // sumia entre o título e a tabela ("agora| Processo |").
        var evt = new LlmStreamEvent(Type: "token", Content: "\n\n");

        Assert.That(AiChatStreamingOrchestrator.IsTextToken(evt), Is.True);
    }

    [Test]
    public void TextToken_MultiCharStartingWithSpace_IsPropagated()
    {
        // Delta multi-char começando com espaço (" 24h") também deve passar.
        var evt = new LlmStreamEvent(Type: "token", Content: " 24h");

        Assert.That(AiChatStreamingOrchestrator.IsTextToken(evt), Is.True);
    }

    [Test]
    public void TextToken_EmptyContent_IsNotPropagated()
    {
        Assert.That(
            AiChatStreamingOrchestrator.IsTextToken(new LlmStreamEvent(Type: "token", Content: "")),
            Is.False);
        Assert.That(
            AiChatStreamingOrchestrator.IsTextToken(new LlmStreamEvent(Type: "token", Content: null)),
            Is.False);
    }

    [Test]
    public void NonTokenEvent_IsNotPropagated()
    {
        Assert.That(
            AiChatStreamingOrchestrator.IsTextToken(new LlmStreamEvent(Type: "done", Content: "x")),
            Is.False);
        Assert.That(
            AiChatStreamingOrchestrator.IsTextToken(new LlmStreamEvent(Type: "tool_calls", Content: "x")),
            Is.False);
    }
}

using System.Text;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Tetos do lado do SERVIDOR para mensagens A2UI. O cliente já limita 6
/// mensagens por turno; sem teto no servidor um LLM "empolgado" enviava tudo
/// pelo SSE e o descarte acontecia longe do usuário (silencioso).
/// </summary>
public class AiChatA2uiExtractorLimitsTests
{
    private static string ComponentLine(int i) =>
        $"{{\"version\":\"v0.9\",\"updateComponents\":{{\"surfaceId\":\"s\",\"components\":[{{\"id\":\"c{i}\",\"component\":\"Text\",\"text\":\"item {i}\"}}]}}}}";

    [Test]
    public void Extract_WhenMoreThanMaxMessages_EmitsAtMostTheCap()
    {
        var block = new StringBuilder();
        block.Append("```a2ui\n");
        block.Append("{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"s\",\"catalogId\":\"basic\"}}\n");
        for (var i = 0; i < AiChatA2uiExtractor.MaxA2uiMessagesPerResponse + 4; i++)
            block.Append(ComponentLine(i)).Append('\n');
        block.Append("```\n");

        var (clean, messages) = AiChatA2uiExtractor.Extract("Card:\n\n" + block + "\nFim.");

        Assert.That(messages, Has.Count.EqualTo(AiChatA2uiExtractor.MaxA2uiMessagesPerResponse));
        Assert.That(messages[0], Does.Contain("createSurface"));
        Assert.That(clean, Does.Not.Contain("a2ui"));
        Assert.That(clean, Does.Contain("Fim."));
    }

    [Test]
    public void Extract_WhenSingleMessageExceedsByteCap_IsSkipped()
    {
        var huge = new string('x', AiChatA2uiExtractor.MaxA2uiMessageBytes + 1);
        var content = "```a2ui\n" +
            "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"s\",\"text\":\"" + huge + "\"}}\n" +
            "```\n";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Is.Empty, "linha acima do teto de bytes não deve virar mensagem");
        Assert.That(clean, Does.Not.Contain("a2ui"));
    }

    [Test]
    public void Extract_WhenUnclosedBlock_HasMessagesCapped()
    {
        var block = new StringBuilder();
        block.Append("```a2ui\n");
        for (var i = 0; i < AiChatA2uiExtractor.MaxA2uiMessagesPerResponse + 3; i++)
            block.Append(ComponentLine(i)).Append('\n');

        var (_, messages) = AiChatA2uiExtractor.Extract(block.ToString());

        Assert.That(messages, Has.Count.EqualTo(AiChatA2uiExtractor.MaxA2uiMessagesPerResponse));
    }
}

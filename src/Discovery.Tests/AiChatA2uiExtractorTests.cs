using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

public class AiChatA2uiExtractorTests
{
    [Test]
    public void Extract_WhenNoA2uiBlock_ReturnsContentUnchangedAndNoMessages()
    {
        const string content = "Olá! Aqui está a resposta em markdown.\n\n**Negrito** e *itálico*.";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(clean, Is.EqualTo(content));
        Assert.That(messages, Is.Empty);
    }

    [Test]
    public void Extract_WhenSingleA2uiBlock_RemovesBlockAndReturnsMessages()
    {
        const string content = "Aqui está o inventário:\n\n```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"inv\",\"catalogId\":\"basic\"}}\n" +
            "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"inv\",\"components\":[]}}\n" +
            "```\n\nEspero que ajude!";

        var (clean, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Has.Count.EqualTo(2));
        Assert.That(messages[0], Does.Contain("\"createSurface\""));
        Assert.That(messages[1], Does.Contain("\"updateComponents\""));
        // O bloco a2ui é removido do texto visível
        Assert.That(clean, Does.Not.Contain("```a2ui"));
        Assert.That(clean, Does.Not.Contain("createSurface"));
        Assert.That(clean, Does.Contain("Aqui está o inventário"));
        Assert.That(clean, Does.Contain("Espero que ajude!"));
    }

    [Test]
    public void Extract_WhenInvalidJsonInBlock_IgnoresInvalidLines()
    {
        const string content = "```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"x\"}}\n" +
            "isto não é json\n" +
            "{\"semVerbo\":true}\n" +
            "```";

        var (_, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(messages[0], Does.Contain("\"createSurface\""));
    }

    [Test]
    public void Extract_WhenUnclosedBlock_ProcessesRemainingLines()
    {
        const string content = "```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"x\"}}\n";

        var (_, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Has.Count.EqualTo(1));
    }

    [Test]
    public void Extract_WhenMultipleBlocks_ReturnsAllMessages()
    {
        const string content = "```a2ui\n" +
            "{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"a\"}}\n" +
            "```\ntexto\n" +
            "```a2ui\n" +
            "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"a\"}}\n" +
            "```";

        var (_, messages) = AiChatA2uiExtractor.Extract(content);

        Assert.That(messages, Has.Count.EqualTo(2));
    }

    [Test]
    public void Extract_WhenNullOrEmpty_ReturnsEmpty()
    {
        var (clean1, m1) = AiChatA2uiExtractor.Extract(null!);
        var (clean2, m2) = AiChatA2uiExtractor.Extract("");

        Assert.That(clean1, Is.EqualTo(string.Empty));
        Assert.That(m1, Is.Empty);
        Assert.That(clean2, Is.EqualTo(string.Empty));
        Assert.That(m2, Is.Empty);
    }
}
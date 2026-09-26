using Discovery.Core.Entities;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// O prompt passou a orientar o uso de knowledge_list e a proibir concluir que a
/// base está vazia a partir de uma busca sem resultado.
/// </summary>
public class AiChatKnowledgePromptTests
{
    private static Agent BuildAgent() => new()
    {
        Id = Guid.NewGuid(),
        Hostname = "DESKTOP-TEST",
        OperatingSystem = "Windows 11",
        SiteId = Guid.NewGuid()
    };

    [Test]
    public void DefaultPrompt_MentionsKnowledgeList()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        Assert.That(prompt, Does.Contain("knowledge_list"));
    }

    [Test]
    public void DefaultPrompt_ForbidsClaimingEmptyBaseFromEmptySearch()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        Assert.That(prompt, Does.Contain("NUNCA afirme que a base está vazia"));
    }

    [Test]
    public void DefaultPrompt_ExplainsKnowledgeArticleDeepLink()
    {
        var prompt = AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(BuildAgent());

        Assert.That(prompt, Does.Contain("knowledge_article"));
    }
}

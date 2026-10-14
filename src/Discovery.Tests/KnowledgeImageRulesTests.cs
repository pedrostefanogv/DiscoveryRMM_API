using Discovery.Core.ValueObjects;

namespace Discovery.Tests;

/// <summary>
/// Regras fechadas das imagens de artigo: limites fixos evitam migração e tela
/// de configuração novas, e o caso de uso é foto de procedimento (print/foto).
/// </summary>
public class KnowledgeImageRulesTests
{
    [TestCase("image/jpeg")]
    [TestCase("IMAGE/PNG")]
    [TestCase(" image/webp ")]
    [TestCase("image/gif")]
    public void IsAllowedContentType_AcceptsSupportedImages(string contentType)
        => Assert.That(KnowledgeImageRules.IsAllowedContentType(contentType), Is.True);

    [TestCase("image/svg+xml")]
    [TestCase("application/pdf")]
    [TestCase("text/html")]
    [TestCase("")]
    [TestCase(null)]
    public void IsAllowedContentType_RejectsEverythingElse(string? contentType)
        => Assert.That(KnowledgeImageRules.IsAllowedContentType(contentType), Is.False);

    [Test]
    public void IsAllowedSize_UsesFiveMegabyteCeiling()
    {
        Assert.That(KnowledgeImageRules.IsAllowedSize(1), Is.True);
        Assert.That(KnowledgeImageRules.IsAllowedSize(KnowledgeImageRules.MaxFileSizeBytes), Is.True);
        Assert.That(KnowledgeImageRules.IsAllowedSize(KnowledgeImageRules.MaxFileSizeBytes + 1), Is.False);
        Assert.That(KnowledgeImageRules.IsAllowedSize(0), Is.False);
    }

    [Test]
    public void BuildMarkdownToken_RemovesBracketsFromAltText()
    {
        var token = KnowledgeImageRules.BuildMarkdownToken("[print] da tela", Guid.Empty);

        Assert.That(token, Is.EqualTo("![print da tela](kbimage:00000000-0000-0000-0000-000000000000)"));
    }

    [Test]
    public void BuildMarkdownToken_AppendsOnlyValidatedSize()
    {
        Assert.That(KnowledgeImageRules.BuildMarkdownToken("x", Guid.Empty, "50%"),
            Is.EqualTo("![x](kbimage:00000000-0000-0000-0000-000000000000 \"w=50%\")"));

        Assert.That(KnowledgeImageRules.BuildMarkdownToken("x", Guid.Empty, "420"),
            Is.EqualTo("![x](kbimage:00000000-0000-0000-0000-000000000000 \"w=420\")"));

        // Fora do padrão: token sem tamanho (largura natural).
        Assert.That(KnowledgeImageRules.BuildMarkdownToken("x", Guid.Empty, "999%"),
            Is.EqualTo("![x](kbimage:00000000-0000-0000-0000-000000000000)"));
    }

    [Test]
    public void ReadUrlTtl_IsShortEnoughToLimitLeakedLinks()
        => Assert.That(KnowledgeImageRules.ReadUrlTtlHours, Is.InRange(1, 6));
}

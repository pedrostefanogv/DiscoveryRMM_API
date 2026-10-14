using Discovery.Core.Helpers;
using Discovery.Core.ValueObjects;

namespace Discovery.Tests;

/// <summary>
/// Token de imagem da base de conhecimento (![alt](kbimage:&lt;attachmentId&gt;)).
/// O token é a referência estável do Markdown; tudo que vai ao LLM ou é
/// indexado precisa removê-lo.
/// </summary>
public class KnowledgeImageMarkdownTests
{
    private static readonly Guid ImageA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ImageB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static string Markdown(Guid first, Guid? second = null)
    {
        var text = "# Título\n\n" + KnowledgeImageRules.BuildMarkdownToken("Tela inicial", first) + "\n\nPasso 1.";
        if (second is { } other)
            text += "\n\n" + KnowledgeImageRules.BuildMarkdownToken("Segunda tela", other);
        return text;
    }

    [Test]
    public void ExtractIds_ReturnsIdsWithoutDuplicates()
    {
        var markdown = Markdown(ImageA) + "\n\n" + KnowledgeImageRules.BuildMarkdownToken("repetida", ImageA);

        var ids = KnowledgeImageMarkdown.ExtractIds(markdown);

        Assert.That(ids, Has.Count.EqualTo(1));
        Assert.That(ids[0], Is.EqualTo(ImageA));
    }

    [Test]
    public void ExtractIds_IgnoresInvalidIdentifiersAndEmptyMarkdown()
    {
        var markdown = "![x](kbimage:nao-e-guid)\n\n![y](kbimage:33333333-3333-3333-3333-333333333333)";

        Assert.That(KnowledgeImageMarkdown.ExtractIds(markdown), Has.Count.EqualTo(1));
        Assert.That(KnowledgeImageMarkdown.ExtractIds(null), Is.Empty);
        Assert.That(KnowledgeImageMarkdown.ExtractIds(string.Empty), Is.Empty);
    }

    [Test]
    public void StripTokens_RemovesImageAndKeepsText()
    {
        var stripped = KnowledgeImageMarkdown.StripTokens(Markdown(ImageA, ImageB));

        Assert.That(stripped, Does.Not.Contain("kbimage"));
        Assert.That(stripped, Does.Contain("# Título"));
        Assert.That(stripped, Does.Contain("Passo 1."));
    }

    [Test]
    public void UnclosedToken_DoesNotSwallowFollowingProse()
    {
        // Regressão: o sufixo tolerante era [^)]* (atravessava linhas) e apagava
        // a prosa até o próximo ")" do documento — inclusive no RemoveToken, que
        // GRAVA o conteúdo do artigo.
        var doc = "Antes\n\n![print](kbimage:" + ImageA.ToString("D")
            + " sem fechar\n\nA senha e secreta (nao compartilhe)\n\nDepois";

        Assert.That(KnowledgeImageMarkdown.StripTokens(doc), Does.Contain("A senha e secreta"));
        Assert.That(KnowledgeImageMarkdown.RemoveToken(doc, ImageA), Does.Contain("A senha e secreta"));
    }

    [Test]
    public void RemoveToken_RemovesOnlyTargetImage()
    {
        var removed = KnowledgeImageMarkdown.RemoveToken(Markdown(ImageA, ImageB), ImageA);

        Assert.That(KnowledgeImageMarkdown.ContainsToken(removed), Is.True);
        Assert.That(KnowledgeImageMarkdown.ExtractIds(removed), Is.EqualTo(new[] { ImageB }));
    }

    [Test]
    public void TokenWithMalformedTitle_IsStillStrippedAndExtracted()
    {
        // Título malformado escrito à mão: sem tolerar o sufixo antes do ")", o
        // token escapava do strip (ia para o LLM) e o inUse ficaria errado.
        var malformed = "![a](kbimage:" + ImageA.ToString("D") + " \"w=50%\" extra)";

        Assert.That(KnowledgeImageMarkdown.ContainsToken(malformed), Is.True);
        Assert.That(KnowledgeImageMarkdown.ExtractIds(malformed), Has.Count.EqualTo(1));
        Assert.That(KnowledgeImageMarkdown.StripTokens(malformed), Does.Not.Contain("kbimage"));
        Assert.That(KnowledgeImageMarkdown.RemoveToken(malformed, ImageA), Does.Not.Contain("kbimage"));
    }

    [Test]
    public void TokenWithSize_IsExtractedStrippedAndRemovedInBothForms()
    {
        var sized = KnowledgeImageRules.BuildMarkdownToken("print", ImageA, "50%");
        Assert.That(sized, Is.EqualTo("![print](kbimage:11111111-1111-1111-1111-111111111111 \"w=50%\")"));

        var markdown = Markdown(ImageA) + "\n\n" + sized + "\n\n"
            + KnowledgeImageRules.BuildMarkdownToken("px", ImageB, "420");

        Assert.That(KnowledgeImageMarkdown.ContainsToken(markdown), Is.True);
        Assert.That(KnowledgeImageMarkdown.ExtractIds(markdown), Has.Count.EqualTo(2));

        var stripped = KnowledgeImageMarkdown.StripTokens(markdown);
        Assert.That(stripped, Does.Not.Contain("kbimage"));
        Assert.That(stripped, Does.Not.Contain("w=50%"));

        // RemoveToken apaga o anexo nas DUAS formas (com e sem tamanho).
        var removed = KnowledgeImageMarkdown.RemoveToken(markdown, ImageA);
        Assert.That(removed, Does.Not.Contain(ImageA.ToString("D")));
        Assert.That(removed, Does.Contain(ImageB.ToString("D")));
    }

    [TestCase("50%", "50%")]
    [TestCase("w=50%", "50%")]
    [TestCase(" 420 ", "420")]
    [TestCase("w=420", "420")]
    [TestCase("0%", null)]
    [TestCase("101%", null)]
    [TestCase("5000", null)]
    [TestCase("abc", null)]
    [TestCase("", null)]
    [TestCase(null, null)]
    public void NormalizeSize_AcceptsOnlyBoundedPercentOrPixels(string? raw, string? expected)
        => Assert.That(KnowledgeImageMarkdown.NormalizeSize(raw), Is.EqualTo(expected));

    [Test]
    public void ContainsToken_DetectsCanonicalTokenFromRules()
    {
        var token = KnowledgeImageRules.BuildMarkdownToken("alt", ImageA);

        Assert.That(token, Is.EqualTo("![alt](kbimage:11111111-1111-1111-1111-111111111111)"));
        Assert.That(KnowledgeImageMarkdown.ContainsToken("# artigo\n\n" + token), Is.True);
        Assert.That(KnowledgeImageMarkdown.ContainsToken("texto sem imagem"), Is.False);
    }
}

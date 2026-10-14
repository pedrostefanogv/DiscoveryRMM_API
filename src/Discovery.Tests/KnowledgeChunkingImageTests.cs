using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Imagens não entram no texto indexado/embedado: o token
/// (![alt](kbimage:&lt;id&gt;)) não é conteúdo semântico e, se vazasse, apareceria
/// como resultado de busca e no trecho entregue ao LLM.
/// </summary>
public class KnowledgeChunkingImageTests
{
    [Test]
    public void ChunkArticle_DoesNotIndexImageTokens()
    {
        var attachmentId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var article = new KnowledgeArticle
        {
            Id = Guid.NewGuid(),
            Title = "Trocar toner",
            Content = "## Passo a passo\n\n"
                + KnowledgeImageRules.BuildMarkdownToken("Tampa aberta", attachmentId)
                + "\n\n"
                + string.Join(" ", Enumerable.Repeat("remova o toner antigo", 300))
        };

        var chunks = new KnowledgeChunkingService().ChunkArticle(article);

        Assert.That(chunks, Is.Not.Empty);
        Assert.That(chunks.All(c => !c.Content.Contains("kbimage")), Is.True);
        Assert.That(chunks.All(c => !c.Content.Contains(attachmentId.ToString("D"))), Is.True);
        Assert.That(chunks.Any(c => c.Content.Contains("remova o toner antigo")), Is.True);
    }
}

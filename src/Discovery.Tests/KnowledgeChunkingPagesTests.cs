using Discovery.Core.Entities;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// O chunking passou a incluir o conteúdo das sub-páginas (estilo Notion) para
/// que ele seja indexado e recuperável na busca semântica.
/// </summary>
public class KnowledgeChunkingPagesTests
{
    private static KnowledgeArticle BuildLongArticle() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Manual de TI",
        Content = "## Introdução\n\n" + string.Join(" ", Enumerable.Repeat("instrucao", 700))
    };

    private static KnowledgeArticlePage BuildPage(Guid articleId) => new()
    {
        Id = Guid.NewGuid(),
        ArticleId = articleId,
        Title = "Instalar Foxit Reader",
        Content = "Baixe o instalador e execute o assistente de instalação passo a passo.",
        SortOrder = 0
    };

    [Test]
    public void ChunkArticle_WithPages_IndexesPageContentAndSectionTitle()
    {
        var service = new KnowledgeChunkingService();
        var article = BuildLongArticle();
        var page = BuildPage(article.Id);

        var chunks = service.ChunkArticle(article, [page]);

        Assert.That(chunks, Has.Some.Matches<KnowledgeArticleChunk>(c =>
            c.SectionTitle == "Instalar Foxit Reader" &&
            c.Content.Contains("assistente de instalação")));
    }

    [Test]
    public void ChunkArticle_WithoutPages_DoesNotIncludePageContent()
    {
        var service = new KnowledgeChunkingService();
        var article = BuildLongArticle();
        var page = BuildPage(article.Id);

        var chunks = service.ChunkArticle(article);

        Assert.That(chunks.All(c => !c.Content.Contains("assistente de instalação")), Is.True);
        Assert.That(chunks.Any(c => c.SectionTitle == page.Title), Is.False);
    }

    [Test]
    public void ChunkArticle_ShortArticleWithPage_ReturnsSingleCombinedChunk()
    {
        var service = new KnowledgeChunkingService();
        var article = new KnowledgeArticle { Id = Guid.NewGuid(), Title = "Curto", Content = "Resumo curto." };
        var page = BuildPage(article.Id);

        var chunks = service.ChunkArticle(article, [page]);

        Assert.That(chunks, Has.Count.EqualTo(1));
        Assert.That(chunks[0].Content, Does.Contain("Resumo curto."));
        Assert.That(chunks[0].Content, Does.Contain("assistente de instalação"));
    }
}

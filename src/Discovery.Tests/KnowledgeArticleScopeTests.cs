using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Visibilidade de imagem de artigo para o agente: mesma regra (e mesma
/// semântica de herança) da listagem de artigos — site → client → global.
/// Precisa bater com IKnowledgeArticleRepository.ListByUserScopeAsync, senão o
/// agente conseguiria baixar imagem de artigo que não enxerga na lista.
/// </summary>
public class KnowledgeArticleScopeTests
{
    private static readonly Guid AgentClient = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherClient = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AgentSite = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid OtherSite = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static KnowledgeArticle Article(string status = "Published", Guid? clientId = null, Guid? siteId = null)
        => new() { Id = Guid.NewGuid(), Title = "Manual", Content = "conteudo", Status = status, ClientId = clientId, SiteId = siteId };

    [Test]
    public void GlobalPublishedArticle_IsVisibleToAnyAgent()
        => Assert.That(KnowledgeArticleScope.IsVisibleToAgent(Article(), AgentClient, AgentSite), Is.True);

    [Test]
    public void GlobalPublishedArticle_IsVisibleEvenWithoutClientResolution()
        => Assert.That(KnowledgeArticleScope.IsVisibleToAgent(Article(), null, AgentSite), Is.True);

    [Test]
    public void ClientArticle_RequiresMatchingClient()
    {
        Assert.That(KnowledgeArticleScope.IsVisibleToAgent(Article(clientId: AgentClient), AgentClient, AgentSite), Is.True);
        Assert.That(KnowledgeArticleScope.IsVisibleToAgent(Article(clientId: OtherClient), AgentClient, AgentSite), Is.False);
        Assert.That(KnowledgeArticleScope.IsVisibleToAgent(Article(clientId: OtherClient), null, AgentSite), Is.False);
    }

    [Test]
    public void SiteArticle_RequiresMatchingSite()
    {
        Assert.That(
            KnowledgeArticleScope.IsVisibleToAgent(Article(clientId: AgentClient, siteId: AgentSite), AgentClient, AgentSite),
            Is.True);
        Assert.That(
            KnowledgeArticleScope.IsVisibleToAgent(Article(clientId: AgentClient, siteId: OtherSite), AgentClient, AgentSite),
            Is.False);
    }

    [Test]
    public void UnpublishedArticles_AreNeverVisibleToAgents()
    {
        Assert.That(
            KnowledgeArticleScope.IsVisibleToAgent(Article(status: ArticleStatus.Draft.ToString()), AgentClient, AgentSite),
            Is.False);
        Assert.That(
            KnowledgeArticleScope.IsVisibleToAgent(Article(status: ArticleStatus.Internal.ToString()), AgentClient, AgentSite),
            Is.False);
    }

    [Test]
    public void NullArticle_IsNotVisible()
        => Assert.That(KnowledgeArticleScope.IsVisibleToAgent(null, AgentClient, AgentSite), Is.False);
}

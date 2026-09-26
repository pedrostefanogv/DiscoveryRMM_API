using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Testes da tool knowledge_list (catálogo do chat). Contexto: sem uma tool de
/// listagem o LLM não tinha como responder "quais artigos existem?" e concluía
/// que a base estava vazia.
/// </summary>
public class KnowledgeListToolTests
{
    private static KnowledgeArticle BuildArticle(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Content = "Primeira linha do artigo.\n\nDetalhes.",
        Category = "Softwares",
        TagsJson = "[\"foxit\",\"pdf\"]",
        Status = "Published",
        UpdatedAt = DateTime.UtcNow
    };

    private static KnowledgeListMcpTool BuildTool(FakeArticleRepository repo)
        => new(repo, NullLogger<KnowledgeListMcpTool>.Instance);

    [Test]
    public async Task Execute_ReturnsCatalogWithTitlesAndCount()
    {
        var repo = new FakeArticleRepository
        {
            Page = new ArticleListPageData { Items = [BuildArticle("Instalar Foxit Reader")], HasMore = false },
            Total = 3
        };

        var json = await BuildTool(repo).ExecuteAsync(null, null, null, 50);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.That(root.GetProperty("found").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("count").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("total").GetInt32(), Is.EqualTo(3));
        Assert.That(root.GetProperty("articles")[0].GetProperty("title").GetString(), Is.EqualTo("Instalar Foxit Reader"));
        Assert.That(root.GetProperty("articles")[0].GetProperty("summary").GetString(), Does.Contain("Primeira linha"));
    }

    [Test]
    public async Task Execute_AlwaysRequestsPublishedOnly()
    {
        var repo = new FakeArticleRepository { Page = new ArticleListPageData(), Total = 0 };

        await BuildTool(repo).ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), null, 50);

        Assert.That(repo.CapturedStatus, Is.EqualTo("Published"));
        Assert.That(repo.CapturedClientId, Is.Not.Null);
        Assert.That(repo.CapturedSiteId, Is.Not.Null);
    }

    [Test]
    public async Task Execute_ClampsLimitToRange()
    {
        var repo = new FakeArticleRepository { Page = new ArticleListPageData(), Total = 0 };

        await BuildTool(repo).ExecuteAsync(null, null, null, 0);
        Assert.That(repo.CapturedLimit, Is.EqualTo(50));

        await BuildTool(repo).ExecuteAsync(null, null, null, 500);
        Assert.That(repo.CapturedLimit, Is.EqualTo(100));
    }

    [Test]
    public async Task Execute_EmptyCatalog_ReturnsZeroWithMessage()
    {
        var repo = new FakeArticleRepository { Page = new ArticleListPageData(), Total = 0 };

        var json = await BuildTool(repo).ExecuteAsync(null, null, null, 50);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.That(root.GetProperty("found").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("count").GetInt32(), Is.EqualTo(0));
        Assert.That(root.GetProperty("message").GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Execute_RepositoryFailure_ReturnsErrorJsonWithoutThrowing()
    {
        var repo = new FakeArticleRepository { ThrowOnList = true };

        var json = await BuildTool(repo).ExecuteAsync(null, null, null, 50);
        using var doc = JsonDocument.Parse(json);

        Assert.That(doc.RootElement.GetProperty("found").GetBoolean(), Is.False);
        Assert.That(doc.RootElement.GetProperty("error").GetString(), Is.Not.Null.And.Not.Empty);
    }

    private sealed class FakeArticleRepository : IKnowledgeArticleRepository
    {
        public ArticleListPageData Page { get; set; } = new();
        public int Total { get; set; }
        public bool ThrowOnList { get; set; }

        public string? CapturedStatus { get; private set; }
        public Guid? CapturedClientId { get; private set; }
        public Guid? CapturedSiteId { get; private set; }
        public int CapturedLimit { get; private set; }

        public Task<ArticleListPageData> ListByUserScopeAsync(
            bool hasGlobalAccess, IReadOnlySet<Guid> allowedClientIds, IReadOnlySet<Guid> allowedSiteIds,
            string? status = null, Guid? departmentId = null, string? category = null, string? cursor = null,
            int limit = 20, Guid? filterClientId = null, Guid? filterSiteId = null,
            string? sortBy = null, string? sortDirection = null, CancellationToken ct = default)
        {
            if (ThrowOnList) throw new InvalidOperationException("boom");
            CapturedStatus = status;
            CapturedClientId = filterClientId;
            CapturedSiteId = filterSiteId;
            CapturedLimit = limit;
            return Task.FromResult(Page);
        }

        public Task<int> CountPublishedAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
            => Task.FromResult(Total);

        public Task<KnowledgeArticle?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<KnowledgeArticle> CreateAsync(KnowledgeArticle article, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<KnowledgeArticle> UpdateAsync(KnowledgeArticle article, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> SearchKeywordAsync(string query, Guid? clientId, Guid? siteId, Guid? departmentId = null, bool publishedOnly = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> SearchKeywordByUserScopeAsync(string query, bool hasGlobalAccess, IReadOnlySet<Guid> allowedClientIds, IReadOnlySet<Guid> allowedSiteIds, Guid? departmentId = null, bool publishedOnly = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> GetArticlesNeedingChunkingAsync(int limit = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> HasPublishedArticlesAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<KnowledgeArticleVersion> CreateVersionAsync(KnowledgeArticleVersion version, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticleVersion>> GetVersionsAsync(Guid articleId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<KnowledgeArticleVersion?> GetVersionAsync(Guid articleId, int versionNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TicketKnowledgeLink?> GetLinkAsync(Guid ticketId, Guid articleId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TicketKnowledgeLink> LinkToTicketAsync(Guid ticketId, Guid articleId, string? linkedBy, string? note, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnlinkFromTicketAsync(Guid ticketId, Guid articleId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<TicketKnowledgeLink>> GetTicketLinksAsync(Guid ticketId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TicketKnowledgeLink> UpdateLinkAsync(TicketKnowledgeLink link, CancellationToken ct = default) => throw new NotImplementedException();
    }
}

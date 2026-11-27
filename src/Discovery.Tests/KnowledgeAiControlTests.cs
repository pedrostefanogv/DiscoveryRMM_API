using System.Text.Json;
using Discovery.Api.Controllers;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Knowledge;
using Discovery.Core.Cqrs.Knowledge.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using Discovery.Infrastructure.Cqrs.Knowledge;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Discovery.Tests;

public class KnowledgeAiControlTests
{
    [Test]
    public async Task KnowledgeMcpTool_AiOnly_omits_title_and_link_and_sets_guidance()
    {
        var article = new KnowledgeArticle
        {
            Id = Guid.NewGuid(),
            Title = "Segredo Interno da IA",
            Content = "Passo 1: instrua o usuário a reiniciar o spooler.",
            Status = ArticleStatus.AiOnly.ToString(),
            AiIndexingEnabled = true,
        };

        var chunkRepo = new StubChunkRepository([
            new KnowledgeChunkSearchResult(
                article.Id, article.Title, null, null, "Seção", article.Content, 0.1, ArticleStatus.AiOnly.ToString()),
        ]);
        var articleRepo = new StubArticleRepository([article]);
        var tool = new KnowledgeMcpTool(
            chunkRepo,
            articleRepo,
            new StubEmbeddingProvider(),
            new StubConfigResolver(),
            new StubCredentialResolver(),
            NullLogger<KnowledgeMcpTool>.Instance);

        var json = await tool.ExecuteAsync(null, null, "spooler");
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.GetProperty("results")[0];

        Assert.That(first.GetProperty("ai_only").GetBoolean(), Is.True);
        Assert.That(first.TryGetProperty("title", out _), Is.False);
        Assert.That(first.TryGetProperty("internal_url", out _), Is.False);
        Assert.That(first.TryGetProperty("citation", out _), Is.False);
        Assert.That(first.GetProperty("guidance").GetString(), Does.Contain("nunca cite"));
    }

    [Test]
    public async Task KnowledgeListMcpTool_excludes_articles_with_AiIndexingEnabled_false()
    {
        var allowed = new KnowledgeArticle
        {
            Id = Guid.NewGuid(),
            Title = "Artigo Permitido",
            Content = "Conteúdo A",
            Status = ArticleStatus.Published.ToString(),
            AiIndexingEnabled = true,
            UpdatedAt = DateTime.UtcNow,
        };
        var blocked = new KnowledgeArticle
        {
            Id = Guid.NewGuid(),
            Title = "Artigo Bloqueado",
            Content = "Conteúdo B",
            Status = ArticleStatus.Published.ToString(),
            AiIndexingEnabled = false,
            UpdatedAt = DateTime.UtcNow,
        };

        var repo = new StubArticleRepository([allowed, blocked]);
        var tool = new KnowledgeListMcpTool(repo, NullLogger<KnowledgeListMcpTool>.Instance);

        var json = await tool.ExecuteAsync(null, null, null, 20);
        using var doc = JsonDocument.Parse(json);
        var articles = doc.RootElement.GetProperty("articles");

        Assert.That(articles.GetArrayLength(), Is.EqualTo(1));
        Assert.That(articles[0].GetProperty("title").GetString(), Is.EqualTo("Artigo Permitido"));
    }

    [Test]
    public async Task AgentKnowledgeHandlers_block_AiOnly_and_Internal_articles()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"kb-agent-scope-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ReducedDbContext(options);

        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site" });
        db.Agents.Add(new Agent { Id = agentId, SiteId = siteId, Hostname = "pc-01", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var aiOnlyArticle = new KnowledgeArticle
        {
            Id = Guid.NewGuid(),
            Title = "Somente IA",
            Content = "Secreto",
            Status = ArticleStatus.AiOnly.ToString(),
        };

        var agentRepo = new AgentRepository(db);
        var siteRepo = new SiteRepository(db);
        var articleRepo = new StubArticleRepository([aiOnlyArticle]);
        var pageRepo = new StubPageRepository();

        var articleHandler = new GetKnowledgeArticleHandler(agentRepo, siteRepo, articleRepo);
        var pagesHandler = new GetKnowledgeArticlePagesHandler(agentRepo, siteRepo, articleRepo, pageRepo);

        var r1 = await articleHandler.Handle(new GetKnowledgeArticleQuery(agentId, aiOnlyArticle.Id), CancellationToken.None);
        var r2 = await pagesHandler.Handle(new GetKnowledgeArticlePagesQuery(agentId, aiOnlyArticle.Id), CancellationToken.None);

        Assert.That(r1.IsSuccess, Is.False);
        Assert.That(r2.IsSuccess, Is.False);
    }

    [Test]
    public async Task UpdateKnowledgeArticleCommandHandler_updates_scope_and_clears_chunks_when_AI_disabled()
    {
        var article = new KnowledgeArticle
        {
            Id = Guid.NewGuid(),
            Title = "Título",
            Content = "Conteúdo",
            Status = ArticleStatus.Published.ToString(),
            AiIndexingEnabled = true,
            Chunks = [new KnowledgeArticleChunk { Id = Guid.NewGuid(), Content = "c1", EmbeddingGeneratedAt = DateTime.UtcNow }],
        };
        var clientId = Guid.NewGuid();
        var deptId = Guid.NewGuid();

        var repo = new StubArticleRepository([article]);
        var chunkRepo = new StubChunkRepository([]);
        var queueRepo = new StubQueueRepository();
        var handler = new UpdateKnowledgeArticleCommandHandler(repo, chunkRepo, queueRepo);

        var result = await handler.Handle(new UpdateKnowledgeArticleCommand(
            article.Id, "Novo Título", "Novo Conteúdo", "Rede", ["vpn"], "admin",
            ClientId: clientId, SiteId: null, DepartmentId: deptId,
            AiIndexingEnabled: false, UpdateScope: true), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.ChunkCount, Is.EqualTo(0));
        Assert.That(result.Value.EmbeddedChunkCount, Is.EqualTo(0));
        Assert.That(article.ClientId, Is.EqualTo(clientId));
        Assert.That(article.DepartmentId, Is.EqualTo(deptId));
        Assert.That(article.AiIndexingEnabled, Is.False);
        Assert.That(chunkRepo.ClearedArticleIds, Does.Contain(article.Id));
        Assert.That(queueRepo.Enqueued, Is.Empty);
    }

    [Test]
    public async Task GetKnowledgeArticleVersionsQueryHandler_preserves_tags()
    {
        var articleId = Guid.NewGuid();
        var version = new KnowledgeArticleVersion
        {
            Id = Guid.NewGuid(),
            ArticleId = articleId,
            VersionNumber = 1,
            Title = "v1",
            Content = "Texto",
            TagsJson = "[\"vpn\",\"rede\"]",
            Status = ArticleStatus.Published.ToString(),
        };
        var repo = new StubArticleRepository([], [version]);
        var handler = new GetKnowledgeArticleVersionsQueryHandler(repo);

        var result = await handler.Handle(new GetKnowledgeArticleVersionsQuery(articleId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Single().Tags, Is.EqualTo(new[] { "vpn", "rede" }));
    }

    [Test]
    public async Task PublishKnowledgeArticleCommandHandler_validates_department_for_Internal_and_forces_AI_for_AiOnly()
    {
        var article = new KnowledgeArticle
        {
            Id = Guid.NewGuid(),
            Title = "Guia",
            Content = "Conteúdo",
            Status = ArticleStatus.Draft.ToString(),
            AiIndexingEnabled = false,
            DepartmentId = null,
        };
        var repo = new StubArticleRepository([article]);
        var queueRepo = new StubQueueRepository();
        var handler = new PublishKnowledgeArticleCommandHandler(repo, queueRepo);

        var internalFail = await handler.Handle(
            new PublishKnowledgeArticleCommand(article.Id, ArticleStatus.Internal.ToString(), "admin"),
            CancellationToken.None);
        Assert.That(internalFail.IsSuccess, Is.False);

        var aiOnlyOk = await handler.Handle(
            new PublishKnowledgeArticleCommand(article.Id, ArticleStatus.AiOnly.ToString(), "admin"),
            CancellationToken.None);
        Assert.That(aiOnlyOk.IsSuccess, Is.True);
        Assert.That(article.AiIndexingEnabled, Is.True);
        Assert.That(queueRepo.Enqueued, Does.Contain(article.Id));
    }

    [Test]
    public async Task ReindexKnowledgeArticleCommandHandler_rejects_disabled_and_enqueues_enabled()
    {
        var disabled = new KnowledgeArticle
        {
            Id = Guid.NewGuid(),
            Title = "Sem IA",
            Content = "Texto",
            Status = ArticleStatus.Published.ToString(),
            AiIndexingEnabled = false,
        };
        var enabled = new KnowledgeArticle
        {
            Id = Guid.NewGuid(),
            Title = "Com IA",
            Content = "Texto",
            Status = ArticleStatus.Published.ToString(),
            AiIndexingEnabled = true,
        };

        var repo = new StubArticleRepository([disabled, enabled]);
        var chunkRepo = new StubChunkRepository([]);
        var queueRepo = new StubQueueRepository();
        var handler = new ReindexKnowledgeArticleCommandHandler(repo, chunkRepo, queueRepo);

        var r1 = await handler.Handle(new ReindexKnowledgeArticleCommand(disabled.Id), CancellationToken.None);
        var r2 = await handler.Handle(new ReindexKnowledgeArticleCommand(enabled.Id), CancellationToken.None);

        Assert.That(r1.IsSuccess, Is.False);
        Assert.That(r2.IsSuccess, Is.True);
        Assert.That(queueRepo.Enqueued, Is.EqualTo(new[] { enabled.Id }));
    }

    [Test]
    public async Task KnowledgeController_uses_authenticated_username_when_CreatedBy_or_LastEditedBy_is_null()
    {
        var mediator = new CapturingMediator();
        var controller = new KnowledgeController(
            mediator,
            null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.HttpContext.Items["Username"] = "pedro.admin";

        await controller.Create(
            new CreateArticleRequest("Artigo", "Conteúdo completo", null, null, CreatedBy: null, null, null),
            CancellationToken.None);
        var createCmd = mediator.Sent.OfType<CreateKnowledgeArticleCommand>().Single();
        Assert.That(createCmd.CreatedBy, Is.EqualTo("pedro.admin"));

        await controller.Update(
            Guid.NewGuid(),
            new UpdateArticleRequest("Artigo", "Conteúdo atualizado", null, null, LastEditedBy: null),
            CancellationToken.None);
        var updateCmd = mediator.Sent.OfType<UpdateKnowledgeArticleCommand>().Single();
        Assert.That(updateCmd.LastEditedBy, Is.EqualTo("pedro.admin"));
    }

    private sealed class CapturingMediator : IMediator
    {
        public List<object> Sent { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            if (typeof(TResponse) == typeof(Result<ArticleResponse>))
            {
                var dto = new ArticleResponse(
                    Guid.NewGuid(), "Artigo", "Conteúdo", null, [], "pedro.admin", "pedro.admin",
                    DateTime.UtcNow, "Draft", "Global", "global", null, null, null, null, null,
                    0, null, 0, false, DateTime.UtcNow, DateTime.UtcNow);
                return Task.FromResult((TResponse)(object)Result<ArticleResponse>.Success(dto));
            }
            return Task.FromResult(default(TResponse)!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
            => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
            => Task.CompletedTask;
    }

    private sealed class ReducedDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Site), typeof(Agent) };
            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null &&
                                     t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }
        }
    }

    private sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        public Task<float[]> GenerateEmbeddingAsync(string text, string? modelOverride = null, string? apiKeyOverride = null, string? baseUrlOverride = null, CancellationToken ct = default)
            => Task.FromResult(new float[1536]);

        public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, string? modelOverride = null, string? apiKeyOverride = null, string? baseUrlOverride = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[1536]).ToList());
    }

    private sealed class StubConfigResolver : IConfigurationResolver
    {
        public Task<AIIntegrationSettings> GetAISettingsAsync() => Task.FromResult(new AIIntegrationSettings());
        public Task<ServerConfiguration> GetServerAsync() => throw new NotImplementedException();
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotImplementedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotImplementedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotImplementedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotImplementedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotImplementedException();
        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotImplementedException();
        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(Guid? clientId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ValidateInheritanceAsync() => throw new NotImplementedException();
        public void ClearCache() { }
    }

    private sealed class StubCredentialResolver : IAiCredentialResolver
    {
        public Task<ResolvedCredential?> ResolveAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
            => Task.FromResult<ResolvedCredential?>(null);
        public Task<string?> ResolveEmbeddingApiKeyAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> ResolveChatApiKeyAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubChunkRepository(List<KnowledgeChunkSearchResult> results) : IKnowledgeChunkRepository
    {
        public List<Guid> ClearedArticleIds { get; } = [];

        public Task<List<KnowledgeChunkSearchResult>> SearchSemanticAsync(
            Vector queryEmbedding, Guid? clientId, Guid? siteId, int limit = 5, double minSimilarity = 0,
            IReadOnlyCollection<Guid>? excludeArticleIds = null, Guid? departmentId = null, bool publishedOnly = false,
            CancellationToken ct = default, bool forAi = false)
            => Task.FromResult(results);

        public Task<List<KnowledgeChunkSearchResult>> SearchSemanticByUserScopeAsync(
            Vector queryEmbedding, bool hasGlobalAccess, IReadOnlySet<Guid> allowedClientIds, IReadOnlySet<Guid> allowedSiteIds,
            int limit = 5, double minSimilarity = 0, IReadOnlyCollection<Guid>? excludeArticleIds = null,
            Guid? departmentId = null, bool publishedOnly = false, CancellationToken ct = default, bool forAi = false)
            => Task.FromResult(results);

        public Task<List<KnowledgeArticleChunk>> GetChunksWithoutEmbeddingAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult<List<KnowledgeArticleChunk>>([]);

        public Task ReplaceAllForArticleAsync(Guid articleId, List<KnowledgeArticleChunk> newChunks, CancellationToken ct = default)
        {
            if (newChunks.Count == 0) ClearedArticleIds.Add(articleId);
            return Task.CompletedTask;
        }

        public Task UpdateEmbeddingAsync(Guid chunkId, Vector embedding, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> HasAnyChunkAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default) => Task.FromResult(results.Count > 0);
    }

    private sealed class StubQueueRepository : IKnowledgeEmbeddingQueueRepository
    {
        public List<Guid> Enqueued { get; } = [];
        public Task EnqueueAsync(Guid articleId, string? reason, CancellationToken ct = default)
        {
            Enqueued.Add(articleId);
            return Task.CompletedTask;
        }
        public Task<List<KnowledgeEmbeddingQueueItem>> ClaimBatchAsync(int limit, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkDoneAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkFailedAsync(Guid id, string errorMessage, TimeSpan retryDelay, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubArticleRepository(List<KnowledgeArticle> articles, List<KnowledgeArticleVersion>? versions = null) : IKnowledgeArticleRepository
    {
        public Task<KnowledgeArticle?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(articles.FirstOrDefault(a => a.Id == id));

        public Task<KnowledgeArticle?> GetWithoutChunksAsync(Guid id, CancellationToken ct = default)
            => GetByIdAsync(id, ct);

        public Task<KnowledgeArticle> UpdateAsync(KnowledgeArticle article, CancellationToken ct = default)
            => Task.FromResult(article);

        public Task<KnowledgeArticleVersion> CreateVersionAsync(KnowledgeArticleVersion version, CancellationToken ct = default)
            => Task.FromResult(version);

        public Task<ArticleListPageData> ListByUserScopeAsync(
            bool hasGlobalAccess, IReadOnlySet<Guid> allowedClientIds, IReadOnlySet<Guid> allowedSiteIds,
            string? status = null, Guid? departmentId = null, string? category = null, string? cursor = null,
            int limit = 20, Guid? filterClientId = null, Guid? filterSiteId = null,
            string? sortBy = null, string? sortDirection = null, CancellationToken ct = default)
            => Task.FromResult(new ArticleListPageData { Items = articles, Count = articles.Count });

        public Task<int> CountPublishedAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default, bool forAi = false)
            => Task.FromResult(articles.Count);

        public Task<KnowledgeArticle> CreateAsync(KnowledgeArticle article, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> SearchKeywordAsync(string query, Guid? clientId, Guid? siteId, Guid? departmentId = null, bool publishedOnly = false, CancellationToken ct = default, bool forAi = false) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> SearchKeywordByUserScopeAsync(string query, bool hasGlobalAccess, IReadOnlySet<Guid> allowedClientIds, IReadOnlySet<Guid> allowedSiteIds, Guid? departmentId = null, bool publishedOnly = false, CancellationToken ct = default, bool forAi = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListDistinctCategoriesByUserScopeAsync(bool hasGlobalAccess, IReadOnlySet<Guid> allowedClientIds, IReadOnlySet<Guid> allowedSiteIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> GetArticlesNeedingChunkingAsync(int limit = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> HasPublishedArticlesAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default, bool forAi = false) => throw new NotImplementedException();
        public Task<List<KnowledgeArticleVersion>> GetVersionsAsync(Guid articleId, CancellationToken ct = default)
            => Task.FromResult((versions ?? []).Where(v => v.ArticleId == articleId).ToList());
        public Task<KnowledgeArticleVersion?> GetVersionAsync(Guid articleId, int versionNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TicketKnowledgeLink?> GetLinkAsync(Guid ticketId, Guid articleId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TicketKnowledgeLink> LinkToTicketAsync(Guid ticketId, Guid articleId, string? linkedBy, string? note, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnlinkFromTicketAsync(Guid ticketId, Guid articleId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<TicketKnowledgeLink>> GetTicketLinksAsync(Guid ticketId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TicketKnowledgeLink> UpdateLinkAsync(TicketKnowledgeLink link, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubPageRepository : IKnowledgeArticlePageRepository
    {
        public Task<List<KnowledgeArticlePage>> ListByArticleAsync(Guid articleId, CancellationToken ct = default) => Task.FromResult(new List<KnowledgeArticlePage>());
        public Task<KnowledgeArticlePage?> GetByIdAsync(Guid articleId, Guid pageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<KnowledgeArticlePage?> GetByIdWithParentAsync(Guid articleId, Guid pageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<KnowledgeArticlePage> CreateAsync(KnowledgeArticlePage page, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<KnowledgeArticlePage> UpdateAsync(KnowledgeArticlePage page, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(Guid articleId, Guid pageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetDepthAsync(Guid? parentPageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetSubtreeMaxLevelAsync(Guid pageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> IsDescendantAsync(Guid ancestorId, Guid nodeId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}

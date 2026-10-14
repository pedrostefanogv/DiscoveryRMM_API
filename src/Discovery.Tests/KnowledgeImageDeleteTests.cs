using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Knowledge.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Cqrs.Knowledge;

namespace Discovery.Tests;

/// <summary>
/// Exclusão de imagem: soft delete (padrão, com janela de 30 dias) versus
/// exclusão DEFINITIVA (objeto no storage + linha, sem recuperação). O caminho
/// destrutivo precisa de teste próprio — é irreversível.
/// </summary>
public class KnowledgeImageDeleteTests
{
    private static readonly Guid ArticleId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid AttachmentId = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

    private static Attachment Image(string content = "texto") => new()
    {
        Id = AttachmentId,
        EntityType = KnowledgeImageRules.EntityType,
        EntityId = ArticleId,
        FileName = "print.png",
        ContentType = "image/png",
        StorageObjectKey = $"global/knowledgeimage/{ArticleId:N}/attachments/{AttachmentId:N}/print.png",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static KnowledgeArticle ArticleWithImage(string content = "Antes\n\n![print](kbimage:" + "bbbbbbbb-1111-2222-3333-444444444444" + ")\n\nDepois")
        => new() { Id = ArticleId, Title = "Manual", Content = content, Status = "Draft" };

    [Test]
    public async Task PermanentDelete_RemovesObjectAndRow_AndClearsReference()
    {
        var attachments = new FakeAttachmentRepository(Image());
        var service = new FakeAttachmentService();
        var articles = new FakeArticleRepository(ArticleWithImage());
        var pages = new FakePageRepository();

        var result = await new DeleteKnowledgeImageCommandHandler(attachments, service, articles, pages)
            .Handle(new DeleteKnowledgeImageCommand(ArticleId, AttachmentId, Permanent: true), default);

        Assert.That(result.IsSuccess, Is.True);
        // O handler delega ao serviço: é ele quem remove storage + linha (coberto
        // em KnowledgeImagePermanentDeleteServiceTests).
        Assert.That(service.PermanentCalls, Is.EqualTo(1));
        Assert.That(service.SoftDeleteCalls, Is.EqualTo(0));
        Assert.That(articles.Updated!.Content, Does.Not.Contain("kbimage"));
    }

    [Test]
    public async Task DefaultDelete_IsSoftDelete_AndKeepsRow()
    {
        var attachments = new FakeAttachmentRepository(Image());
        var service = new FakeAttachmentService();
        var articles = new FakeArticleRepository(ArticleWithImage());
        var pages = new FakePageRepository();

        var result = await new DeleteKnowledgeImageCommandHandler(attachments, service, articles, pages)
            .Handle(new DeleteKnowledgeImageCommand(ArticleId, AttachmentId), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(service.SoftDeleteCalls, Is.EqualTo(1));
        Assert.That(service.PermanentCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task ImageOfAnotherArticle_IsNotFound()
    {
        var attachments = new FakeAttachmentRepository(Image());
        var service = new FakeAttachmentService();

        var result = await new DeleteKnowledgeImageCommandHandler(
                attachments, service, new FakeArticleRepository(ArticleWithImage()), new FakePageRepository())
            .Handle(new DeleteKnowledgeImageCommand(Guid.NewGuid(), AttachmentId, Permanent: true), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(service.PermanentCalls, Is.EqualTo(0));
    }

    private sealed class FakeAttachmentRepository(Attachment attachment) : IAttachmentRepository
    {
        public Guid? HardDeletedId { get; private set; }

        private Attachment? Current => HardDeletedId == attachment.Id ? null : attachment;

        public Task<Attachment?> GetByIdAsync(Guid attachmentId, CancellationToken ct = default)
            => Task.FromResult(Current is not null && Current.Id == attachmentId ? Current : null);

        public Task<bool> HardDeleteAsync(Guid attachmentId, CancellationToken ct = default)
        {
            HardDeletedId = attachmentId;
            return Task.FromResult(true);
        }

        public Task<Attachment> CreateAsync(Attachment a, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Attachment> UpdateAsync(Attachment a, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Attachment> SoftDeleteAsync(Guid attachmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> SoftDeleteByEntityAsync(string entityType, Guid entityId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Attachment>> GetByEntityAsync(string entityType, Guid entityId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Attachment>> GetByClientIdAsync(Guid clientId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Attachment>> GetSoftDeletedOlderThanAsync(int days, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountByEntityAsync(string entityType, Guid entityId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeAttachmentService : IAttachmentService
    {
        public int SoftDeleteCalls { get; private set; }
        public int PermanentCalls { get; private set; }

        public Task DeleteAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
        {
            SoftDeleteCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> PermanentlyDeleteAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
        {
            PermanentCalls++;
            return Task.FromResult(true);
        }

        public Task<PresignedUploadRequest> PreparePresignedUploadAsync(string entityType, Guid entityId, Guid? clientId, string fileName, string contentType, long fileSizeBytes, int urlTtlMinutes, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Attachment> CompletePresignedUploadAsync(Guid attachmentId, string entityType, Guid entityId, Guid? clientId, string fileName, string contentType, long expectedSizeBytes, string objectKey, string? uploadedBy = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Attachment> UploadAttachmentAsync(string entityType, Guid entityId, Guid? clientId, string fileName, Stream content, string contentType, string? uploadedBy = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Stream> DownloadAttachmentAsync(Guid attachmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetPresignedDownloadUrlAsync(Guid attachmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetPresignedDownloadUrlAsync(Guid attachmentId, int ttlHours, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetPresignedDownloadUrlAsync(Attachment attachment, int ttlHours, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Attachment>> GetAttachmentsForEntityAsync(string entityType, Guid entityId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PermanentlyDeleteExpiredAttachmentsAsync(int olderThanDays, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeArticleRepository(KnowledgeArticle article) : IKnowledgeArticleRepository
    {
        public KnowledgeArticle? Updated { get; private set; }

        public Task<KnowledgeArticle?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<KnowledgeArticle?>(Updated ?? (id == article.Id ? article : null));

        public Task<KnowledgeArticle?> GetWithoutChunksAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<KnowledgeArticle?>(Updated ?? (id == article.Id ? article : null));

        public Task<KnowledgeArticle> UpdateAsync(KnowledgeArticle a, CancellationToken ct = default)
        {
            Updated = a;
            return Task.FromResult(a);
        }

        public Task<KnowledgeArticle> CreateAsync(KnowledgeArticle a, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> SearchKeywordAsync(string query, Guid? clientId, Guid? siteId, Guid? departmentId = null, bool publishedOnly = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> SearchKeywordByUserScopeAsync(string query, bool hasGlobalAccess, IReadOnlySet<Guid> allowedClientIds, IReadOnlySet<Guid> allowedSiteIds, Guid? departmentId = null, bool publishedOnly = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> GetArticlesNeedingChunkingAsync(int limit = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticle>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ArticleListPageData> ListByUserScopeAsync(
            bool hasGlobalAccess,
            IReadOnlySet<Guid> allowedClientIds,
            IReadOnlySet<Guid> allowedSiteIds,
            string? status = null,
            Guid? departmentId = null,
            string? category = null,
            string? cursor = null,
            int limit = 20,
            Guid? filterClientId = null,
            Guid? filterSiteId = null,
            string? sortBy = null,
            string? sortDirection = null,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> HasPublishedArticlesAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountPublishedAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<KnowledgeArticleVersion> CreateVersionAsync(KnowledgeArticleVersion version, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<KnowledgeArticleVersion>> GetVersionsAsync(Guid articleId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<KnowledgeArticleVersion?> GetVersionAsync(Guid articleId, int versionNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TicketKnowledgeLink?> GetLinkAsync(Guid ticketId, Guid articleId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TicketKnowledgeLink> LinkToTicketAsync(Guid ticketId, Guid articleId, string? linkedBy, string? note, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnlinkFromTicketAsync(Guid ticketId, Guid articleId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<TicketKnowledgeLink>> GetTicketLinksAsync(Guid ticketId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TicketKnowledgeLink> UpdateLinkAsync(TicketKnowledgeLink link, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakePageRepository : IKnowledgeArticlePageRepository
    {
        public Task<List<KnowledgeArticlePage>> ListByArticleAsync(Guid articleId, CancellationToken ct = default)
            => Task.FromResult(new List<KnowledgeArticlePage>());

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

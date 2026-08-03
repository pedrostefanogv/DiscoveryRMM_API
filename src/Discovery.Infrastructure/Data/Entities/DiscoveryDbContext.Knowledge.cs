using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// Knowledge Base: articles, chunks, versions, queue and ticket links
public partial class DiscoveryDbContext
{
    static partial void ConfigureKnowledge(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KnowledgeArticle>(entity =>
        {
            entity.ToTable("knowledge_articles");
            entity.HasKey(article => article.Id);

            entity.HasIndex(article => article.ClientId).HasDatabaseName("ix_ka_client_id");
            entity.HasIndex(article => article.SiteId).HasDatabaseName("ix_ka_site_id");
            entity.HasIndex(article => article.DeletedAt).HasDatabaseName("ix_ka_deleted_at");
            entity.HasIndex(article => article.Status).HasDatabaseName("ix_ka_status");
            entity.HasIndex(article => article.DepartmentId).HasDatabaseName("ix_ka_department_id");

            entity.Property(article => article.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(article => article.ClientId).HasColumnName("client_id");
            entity.Property(article => article.SiteId).HasColumnName("site_id");
            entity.Property(article => article.DepartmentId).HasColumnName("department_id");
            entity.Property(article => article.Title).HasColumnName("title").HasMaxLength(500);
            entity.Property(article => article.Content).HasColumnName("content").HasColumnType("text");
            entity.Property(article => article.Category).HasColumnName("category").HasMaxLength(200);
            entity.Property(article => article.TagsJson).HasColumnName("tags_json").HasColumnType("jsonb");
            entity.Property(article => article.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(article => article.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(article => article.LastEditedBy).HasColumnName("last_edited_by").HasMaxLength(256);
            entity.Property(article => article.LastEditedAt).HasColumnName("last_edited_at").HasColumnType("timestamptz");
            entity.Property(article => article.PublishedAt).HasColumnName("published_at").HasColumnType("timestamptz");
            entity.Property(article => article.CurrentVersionNumber).HasColumnName("current_version_number");
            entity.Property(article => article.LastChunkedAt).HasColumnName("last_chunked_at").HasColumnType("timestamptz");
            entity.Property(article => article.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(article => article.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(article => article.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");

            entity.HasOne<Client>().WithMany().HasForeignKey(article => article.ClientId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Site>().WithMany().HasForeignKey(article => article.SiteId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Department>().WithMany().HasForeignKey(article => article.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<KnowledgeArticlePage>(entity =>
        {
            entity.ToTable("knowledge_article_pages");
            entity.HasKey(page => page.Id);

            entity.HasIndex(page => new { page.ArticleId, page.ParentPageId, page.SortOrder }).HasDatabaseName("ix_kap_article_parent");

            entity.Property(page => page.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(page => page.ArticleId).HasColumnName("article_id");
            entity.Property(page => page.ParentPageId).HasColumnName("parent_page_id");
            entity.Property(page => page.Title).HasColumnName("title").HasMaxLength(500);
            entity.Property(page => page.Content).HasColumnName("content").HasColumnType("text");
            entity.Property(page => page.SortOrder).HasColumnName("sort_order");
            entity.Property(page => page.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(page => page.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne(page => page.Article)
                .WithMany()
                .HasForeignKey(page => page.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);

            // Auto-referência: sub-página pai → sub-páginas filhas
            entity.HasOne(page => page.ParentPage)
                .WithMany(page => page.Children)
                .HasForeignKey(page => page.ParentPageId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<KnowledgeArticleChunk>(entity =>
        {
            entity.ToTable("knowledge_article_chunks");
            entity.HasKey(chunk => chunk.Id);

            entity.HasIndex(chunk => chunk.ArticleId).HasDatabaseName("ix_kac_article_id");
            entity.HasIndex(chunk => chunk.EmbeddingGeneratedAt).HasDatabaseName("ix_kac_no_embedding");

            entity.Property(chunk => chunk.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(chunk => chunk.ArticleId).HasColumnName("article_id");
            entity.Property(chunk => chunk.ChunkIndex).HasColumnName("chunk_index");
            entity.Property(chunk => chunk.SectionTitle).HasColumnName("section_title").HasMaxLength(500);
            entity.Property(chunk => chunk.Content).HasColumnName("content").HasColumnType("text");
            entity.Property(chunk => chunk.TokenCount).HasColumnName("token_count");
            entity.Property(chunk => chunk.Embedding).HasColumnName("embedding").HasColumnType("vector(1536)");
            entity.Property(chunk => chunk.EmbeddingGeneratedAt).HasColumnName("embedding_generated_at").HasColumnType("timestamptz");

            entity.HasOne(chunk => chunk.Article)
                .WithMany(article => article.Chunks)
                .HasForeignKey(chunk => chunk.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeArticleVersion>(entity =>
        {
            entity.ToTable("knowledge_article_versions");
            entity.HasKey(version => version.Id);

            entity.HasIndex(version => version.ArticleId).HasDatabaseName("ix_kav_article_id");
            entity.HasIndex(version => version.CreatedAt).HasDatabaseName("ix_kav_created_at");
            entity.HasIndex(version => new { version.ArticleId, version.VersionNumber }).HasDatabaseName("ix_kav_article_version");

            entity.Property(version => version.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(version => version.ArticleId).HasColumnName("article_id");
            entity.Property(version => version.VersionNumber).HasColumnName("version_number");
            entity.Property(version => version.Title).HasColumnName("title").HasMaxLength(500);
            entity.Property(version => version.Content).HasColumnName("content").HasColumnType("text");
            entity.Property(version => version.Category).HasColumnName("category").HasMaxLength(200);
            entity.Property(version => version.TagsJson).HasColumnName("tags_json").HasColumnType("jsonb");
            entity.Property(version => version.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(version => version.EditedBy).HasColumnName("edited_by").HasMaxLength(256);
            entity.Property(version => version.ChangeSummary).HasColumnName("change_summary").HasMaxLength(500);
            entity.Property(version => version.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            entity.HasOne(version => version.Article)
                .WithMany(article => article.Versions)
                .HasForeignKey(version => version.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketKnowledgeLink>(entity =>
        {
            entity.ToTable("ticket_knowledge_links");
            entity.HasKey(link => link.Id);

            entity.HasIndex(link => link.TicketId).HasDatabaseName("ix_tkl_ticket_id");
            entity.HasIndex(link => new { link.TicketId, link.ArticleId }).HasDatabaseName("uq_tkl_ticket_article").IsUnique();

            entity.Property(link => link.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(link => link.TicketId).HasColumnName("ticket_id");
            entity.Property(link => link.ArticleId).HasColumnName("article_id");
            entity.Property(link => link.LinkedBy).HasColumnName("linked_by").HasMaxLength(256);
            entity.Property(link => link.Note).HasColumnName("note").HasMaxLength(2000);
            entity.Property(link => link.LinkedAt).HasColumnName("linked_at").HasColumnType("timestamptz");
            entity.Property(link => link.FeedbackUseful).HasColumnName("feedback_useful");
            entity.Property(link => link.FeedbackAt).HasColumnName("feedback_at").HasColumnType("timestamptz");

            entity.HasOne(link => link.Ticket)
                .WithMany()
                .HasForeignKey(link => link.TicketId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(link => link.Article)
                .WithMany(article => article.TicketLinks)
                .HasForeignKey(link => link.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeEmbeddingQueueItem>(entity =>
        {
            entity.ToTable("knowledge_embedding_queue");
            entity.HasKey(item => item.Id);

            entity.HasIndex(item => item.ArticleId).HasDatabaseName("ux_knowledge_embedding_queue_article").IsUnique();
            entity.HasIndex(item => new { item.Status, item.AvailableAt }).HasDatabaseName("ix_knowledge_embedding_queue_status_available");
            entity.HasIndex(item => item.UpdatedAt).HasDatabaseName("ix_knowledge_embedding_queue_updated_at");

            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(item => item.ArticleId).HasColumnName("article_id");
            entity.Property(item => item.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(item => item.Attempts).HasColumnName("attempts");
            entity.Property(item => item.AvailableAt).HasColumnName("available_at").HasColumnType("timestamptz");
            entity.Property(item => item.LastError).HasColumnName("last_error").HasColumnType("text");
            entity.Property(item => item.Reason).HasColumnName("reason").HasMaxLength(50);
            entity.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<KnowledgeArticle>()
                .WithMany()
                .HasForeignKey(item => item.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

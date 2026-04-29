using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Attachments + Misc: Attachment, AgentReleases, AgentUpdateEvents,
//    AutoTicket, Agent Alerts (PSADT), KnowledgeBase, AiProviderCredentials ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureAttachments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Attachment>(entity =>
        {
            entity.ToTable("attachments");
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.EntityType, a.EntityId }).HasDatabaseName("ix_attachments_entity_type_id");
            entity.HasIndex(a => a.ClientId).HasDatabaseName("ix_attachments_client_id");
            entity.HasIndex(a => a.CreatedAt).HasDatabaseName("ix_attachments_created_at");
            entity.HasIndex(a => a.DeletedAt).HasDatabaseName("ix_attachments_deleted_at");
            entity.HasIndex(a => a.StorageObjectKey).HasDatabaseName("ix_attachments_storage_object_key");

            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.EntityType).HasColumnName("entity_type").HasMaxLength(100);
            entity.Property(a => a.EntityId).HasColumnName("entity_id");
            entity.Property(a => a.ClientId).HasColumnName("client_id");
            entity.Property(a => a.FileName).HasColumnName("file_name").HasMaxLength(500);
            entity.Property(a => a.Description).HasColumnName("description").HasMaxLength(1000);
            entity.Property(a => a.StorageObjectKey).HasColumnName("storage_object_key").HasMaxLength(1000);
            entity.Property(a => a.StorageBucket).HasColumnName("storage_bucket").HasMaxLength(200);
            entity.Property(a => a.ContentType).HasColumnName("content_type").HasMaxLength(200);
            entity.Property(a => a.SizeBytes).HasColumnName("size_bytes");
            entity.Property(a => a.StorageChecksum).HasColumnName("storage_checksum").HasMaxLength(200);
            entity.Property(a => a.StorageProviderType).HasColumnName("storage_provider_type");
            entity.Property(a => a.UploadedBy).HasColumnName("uploaded_by").HasMaxLength(256);
            entity.Property(a => a.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(a => a.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(a => a.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });
    }
}

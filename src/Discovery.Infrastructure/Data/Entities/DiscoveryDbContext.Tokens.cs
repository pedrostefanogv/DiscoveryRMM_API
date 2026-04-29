using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Tokens, Notes & Notifications: AgentToken, DeployToken, EntityNote, AppNotification ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureTokensAndNotes(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentToken>(entity =>
        {
            entity.ToTable("agent_tokens");
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.AgentId).HasDatabaseName("ix_agent_tokens_agent_id");
            entity.HasIndex(t => t.TokenHash).IsUnique();

            entity.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(t => t.AgentId).HasColumnName("agent_id");
            entity.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(128);
            entity.Property(t => t.TokenPrefix).HasColumnName("token_prefix").HasMaxLength(12);
            entity.Property(t => t.Description).HasColumnName("description").HasMaxLength(500);
            entity.Property(t => t.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            entity.Property(t => t.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(t => t.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz");
            entity.Property(t => t.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");

            entity.Ignore(t => t.IsRevoked);
            entity.Ignore(t => t.IsExpired);
            entity.Ignore(t => t.IsValid);

            entity.HasOne<Agent>().WithMany().HasForeignKey(t => t.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeployToken>(entity =>
        {
            entity.ToTable("deploy_tokens");
            entity.HasKey(t => t.Id);
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => t.ExpiresAt).HasDatabaseName("ix_deploy_tokens_expires_at");
            entity.HasIndex(t => new { t.ClientId, t.SiteId }).HasDatabaseName("ix_deploy_tokens_client_site");

            entity.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(t => t.ClientId).HasColumnName("client_id");
            entity.Property(t => t.SiteId).HasColumnName("site_id");
            entity.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(128);
            entity.Property(t => t.TokenPrefix).HasColumnName("token_prefix").HasMaxLength(12);
            entity.Property(t => t.Description).HasColumnName("description").HasMaxLength(500);
            entity.Property(t => t.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            entity.Property(t => t.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(t => t.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz");
            entity.Property(t => t.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
            entity.Property(t => t.UsedCount).HasColumnName("used_count");
            entity.Property(t => t.MaxUses).HasColumnName("max_uses");

            entity.Ignore(t => t.IsRevoked);
            entity.Ignore(t => t.IsExpired);
            entity.Ignore(t => t.IsDepleted);
            entity.Ignore(t => t.IsValid);

            entity.HasOne<Client>().WithMany().HasForeignKey(t => t.ClientId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Site>().WithMany().HasForeignKey(t => t.SiteId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EntityNote>(entity =>
        {
            entity.ToTable("entity_notes");
            entity.HasKey(n => n.Id);
            entity.HasIndex(n => new { n.ClientId, n.CreatedAt }).HasDatabaseName("ix_entity_notes_client_created_at");
            entity.HasIndex(n => new { n.SiteId, n.CreatedAt }).HasDatabaseName("ix_entity_notes_site_created_at");
            entity.HasIndex(n => new { n.AgentId, n.CreatedAt }).HasDatabaseName("ix_entity_notes_agent_created_at");

            entity.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(n => n.ClientId).HasColumnName("client_id");
            entity.Property(n => n.SiteId).HasColumnName("site_id");
            entity.Property(n => n.AgentId).HasColumnName("agent_id");
            entity.Property(n => n.Content).HasColumnName("content").HasMaxLength(4000);
            entity.Property(n => n.Author).HasColumnName("author").HasMaxLength(200);
            entity.Property(n => n.IsPinned).HasColumnName("is_pinned");
            entity.Property(n => n.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(n => n.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Client>().WithMany().HasForeignKey(n => n.ClientId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Site>().WithMany().HasForeignKey(n => n.SiteId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>().WithMany().HasForeignKey(n => n.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AppNotification>(entity =>
        {
            entity.ToTable("app_notifications");
            entity.HasKey(n => n.Id);
            entity.HasIndex(n => n.CreatedAt).HasDatabaseName("ix_app_notifications_created_at");
            entity.HasIndex(n => new { n.Topic, n.CreatedAt }).HasDatabaseName("ix_app_notifications_topic_created");
            entity.HasIndex(n => new { n.RecipientUserId, n.IsRead, n.CreatedAt }).HasDatabaseName("ix_app_notifications_user_read_created");
            entity.HasIndex(n => new { n.RecipientAgentId, n.IsRead, n.CreatedAt }).HasDatabaseName("ix_app_notifications_agent_read_created");
            entity.HasIndex(n => new { n.RecipientKey, n.IsRead, n.CreatedAt }).HasDatabaseName("ix_app_notifications_key_read_created");

            entity.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(n => n.EventType).HasColumnName("event_type").HasMaxLength(120);
            entity.Property(n => n.Topic).HasColumnName("topic").HasMaxLength(120);
            entity.Property(n => n.Severity).HasColumnName("severity").HasConversion<int>();
            entity.Property(n => n.RecipientUserId).HasColumnName("recipient_user_id");
            entity.Property(n => n.RecipientAgentId).HasColumnName("recipient_agent_id");
            entity.Property(n => n.RecipientKey).HasColumnName("recipient_key").HasMaxLength(256);
            entity.Property(n => n.Title).HasColumnName("title").HasMaxLength(200);
            entity.Property(n => n.Message).HasColumnName("message").HasMaxLength(2000);
            entity.Property(n => n.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(n => n.IsRead).HasColumnName("is_read");
            entity.Property(n => n.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(n => n.ReadAt).HasColumnName("read_at").HasColumnType("timestamptz");
            entity.Property(n => n.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
        });
    }
}

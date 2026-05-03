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

        modelBuilder.Entity<AgentRelease>(entity =>
        {
            entity.ToTable("agent_releases");
            entity.HasKey(release => release.Id);
            entity.HasIndex(release => new { release.Version, release.Channel })
                .HasDatabaseName("ux_agent_releases_version_channel")
                .IsUnique();
            entity.HasIndex(release => new { release.Channel, release.IsActive, release.PublishedAtUtc })
                .HasDatabaseName("ix_agent_releases_channel_active_published");

            entity.Property(release => release.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(release => release.Version).HasColumnName("version").HasMaxLength(64);
            entity.Property(release => release.Channel).HasColumnName("channel").HasMaxLength(32);
            entity.Property(release => release.IsActive).HasColumnName("is_active");
            entity.Property(release => release.Mandatory).HasColumnName("mandatory");
            entity.Property(release => release.MinimumSupportedVersion).HasColumnName("minimum_supported_version").HasMaxLength(64);
            entity.Property(release => release.ReleaseNotes).HasColumnName("release_notes").HasColumnType("text");
            entity.Property(release => release.PublishedAtUtc).HasColumnName("published_at_utc").HasColumnType("timestamptz");
            entity.Property(release => release.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(release => release.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(release => release.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(release => release.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
        });

        modelBuilder.Entity<AgentReleaseArtifact>(entity =>
        {
            entity.ToTable("agent_release_artifacts");
            entity.HasKey(artifact => artifact.Id);
            entity.HasIndex(artifact => new { artifact.AgentReleaseId, artifact.Platform, artifact.Architecture, artifact.ArtifactType })
                .HasDatabaseName("ux_agent_release_artifacts_release_platform_arch_type")
                .IsUnique();
            entity.HasIndex(artifact => artifact.StorageObjectKey)
                .HasDatabaseName("ix_agent_release_artifacts_storage_object_key");

            entity.Property(artifact => artifact.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(artifact => artifact.AgentReleaseId).HasColumnName("agent_release_id");
            entity.Property(artifact => artifact.Platform).HasColumnName("platform").HasMaxLength(32);
            entity.Property(artifact => artifact.Architecture).HasColumnName("architecture").HasMaxLength(32);
            entity.Property(artifact => artifact.ArtifactType).HasColumnName("artifact_type").HasConversion<int>();
            entity.Property(artifact => artifact.FileName).HasColumnName("file_name").HasMaxLength(500);
            entity.Property(artifact => artifact.ContentType).HasColumnName("content_type").HasMaxLength(200);
            entity.Property(artifact => artifact.StorageObjectKey).HasColumnName("storage_object_key").HasMaxLength(1000);
            entity.Property(artifact => artifact.StorageBucket).HasColumnName("storage_bucket").HasMaxLength(200);
            entity.Property(artifact => artifact.StorageProviderType).HasColumnName("storage_provider_type");
            entity.Property(artifact => artifact.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(artifact => artifact.SizeBytes).HasColumnName("size_bytes");
            entity.Property(artifact => artifact.SignatureThumbprint).HasColumnName("signature_thumbprint").HasMaxLength(200);
            entity.Property(artifact => artifact.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(artifact => artifact.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne(artifact => artifact.Release)
                .WithMany(release => release.Artifacts)
                .HasForeignKey(artifact => artifact.AgentReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentUpdateEvent>(entity =>
        {
            entity.ToTable("agent_update_events");
            entity.HasKey(updateEvent => updateEvent.Id);
            entity.HasIndex(updateEvent => new { updateEvent.AgentId, updateEvent.OccurredAtUtc })
                .HasDatabaseName("ix_agent_update_events_agent_occurred");
            entity.HasIndex(updateEvent => updateEvent.AgentReleaseId)
                .HasDatabaseName("ix_agent_update_events_release_id");

            entity.Property(updateEvent => updateEvent.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(updateEvent => updateEvent.AgentId).HasColumnName("agent_id");
            entity.Property(updateEvent => updateEvent.AgentReleaseId).HasColumnName("agent_release_id");
            entity.Property(updateEvent => updateEvent.EventType).HasColumnName("event_type").HasConversion<int>();
            entity.Property(updateEvent => updateEvent.CurrentVersion).HasColumnName("current_version").HasMaxLength(64);
            entity.Property(updateEvent => updateEvent.TargetVersion).HasColumnName("target_version").HasMaxLength(64);
            entity.Property(updateEvent => updateEvent.Message).HasColumnName("message").HasMaxLength(1000);
            entity.Property(updateEvent => updateEvent.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
            entity.Property(updateEvent => updateEvent.DetailsJson).HasColumnName("details_json").HasColumnType("jsonb");
            entity.Property(updateEvent => updateEvent.OccurredAtUtc).HasColumnName("occurred_at_utc").HasColumnType("timestamptz");
            entity.Property(updateEvent => updateEvent.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            entity.HasOne(updateEvent => updateEvent.Agent)
                .WithMany()
                .HasForeignKey(updateEvent => updateEvent.AgentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<AgentRelease>()
                .WithMany()
                .HasForeignKey(updateEvent => updateEvent.AgentReleaseId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentUpdateBuild>(entity =>
        {
            entity.ToTable("agent_update_builds");
            entity.HasKey(build => build.Id);
            entity.HasIndex(build => new { build.IsActive, build.Platform, build.Architecture, build.ArtifactType, build.UpdatedAt })
                .HasDatabaseName("ix_agent_update_builds_active_target");
            entity.HasIndex(build => build.StorageObjectKey)
                .HasDatabaseName("ix_agent_update_builds_storage_object_key");

            entity.Property(build => build.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(build => build.Version).HasColumnName("version").HasMaxLength(64);
            entity.Property(build => build.Platform).HasColumnName("platform").HasMaxLength(32);
            entity.Property(build => build.Architecture).HasColumnName("architecture").HasMaxLength(32);
            entity.Property(build => build.ArtifactType).HasColumnName("artifact_type").HasConversion<int>();
            entity.Property(build => build.FileName).HasColumnName("file_name").HasMaxLength(500);
            entity.Property(build => build.ContentType).HasColumnName("content_type").HasMaxLength(200);
            entity.Property(build => build.StorageObjectKey).HasColumnName("storage_object_key").HasMaxLength(1000);
            entity.Property(build => build.StorageBucket).HasColumnName("storage_bucket").HasMaxLength(200);
            entity.Property(build => build.StorageProviderType).HasColumnName("storage_provider_type");
            entity.Property(build => build.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(build => build.SizeBytes).HasColumnName("size_bytes");
            entity.Property(build => build.SignatureThumbprint).HasColumnName("signature_thumbprint").HasMaxLength(200);
            entity.Property(build => build.IsActive).HasColumnName("is_active");
            entity.Property(build => build.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(build => build.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(build => build.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(build => build.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
        });
    }
}

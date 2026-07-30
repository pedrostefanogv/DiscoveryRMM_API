using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Remote Sessions: RemoteSession, RemoteSessionAudit, RemoteSessionRecording ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureRemoteSessions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RemoteSession>(entity =>
        {
            entity.ToTable("remote_sessions");
            entity.HasKey(s => s.Id);

            entity.HasIndex(s => s.AgentId).HasDatabaseName("ix_remote_sessions_agent_id");
            entity.HasIndex(s => s.UserId).HasDatabaseName("ix_remote_sessions_user_id");
            entity.HasIndex(s => s.Status).HasDatabaseName("ix_remote_sessions_status");
            entity.HasIndex(s => s.ExpiresAt).HasDatabaseName("ix_remote_sessions_expires_at");

            entity.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(s => s.AgentId).HasColumnName("agent_id");
            entity.Property(s => s.UserId).HasColumnName("user_id");
            entity.Property(s => s.TenantId).HasColumnName("tenant_id");
            entity.Property(s => s.SiteId).HasColumnName("site_id");
            entity.Property(s => s.Kind).HasColumnName("kind").HasConversion<int>();
            entity.Property(s => s.Transport).HasColumnName("transport").HasConversion<int>();
            entity.Property(s => s.QualityProfile).HasColumnName("quality_profile").HasConversion<int>();
            entity.Property(s => s.Codec).HasColumnName("codec").HasConversion<int>();
            entity.Property(s => s.Status).HasColumnName("status").HasMaxLength(32);
            entity.Property(s => s.NatsSubject).HasColumnName("nats_subject").HasMaxLength(512);
            entity.Property(s => s.WebrtcSessionId).HasColumnName("webrtc_session_id").HasMaxLength(256);
            entity.Property(s => s.RecordingEnabled).HasColumnName("recording_enabled");
            entity.Property(s => s.RecordingId).HasColumnName("recording_id");
            entity.Property(s => s.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            entity.Property(s => s.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            entity.Property(s => s.ClosedAt).HasColumnName("closed_at").HasColumnType("timestamptz");
            entity.Property(s => s.DurationSeconds).HasColumnName("duration_seconds");
            entity.Property(s => s.FramesSent).HasColumnName("frames_sent");
            entity.Property(s => s.BytesSent).HasColumnName("bytes_sent");
            entity.Property(s => s.Note).HasColumnName("note").HasMaxLength(2000);

            entity.HasOne(s => s.Agent).WithMany().HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(s => s.Recording).WithOne(r => r.RemoteSession).HasForeignKey<RemoteSessionRecording>(r => r.RemoteSessionId);
            entity.HasMany(s => s.Audits).WithOne(a => a.RemoteSession).HasForeignKey(a => a.RemoteSessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RemoteSessionAudit>(entity =>
        {
            entity.ToTable("remote_session_audits");
            entity.HasKey(a => a.Id);

            entity.HasIndex(a => a.RemoteSessionId).HasDatabaseName("ix_remote_session_audits_session_id");
            entity.HasIndex(a => a.OccurredAt).HasDatabaseName("ix_remote_session_audits_occurred_at");

            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.RemoteSessionId).HasColumnName("remote_session_id");
            entity.Property(a => a.EventType).HasColumnName("event_type").HasMaxLength(64);
            entity.Property(a => a.ActorUserId).HasColumnName("actor_user_id").HasMaxLength(256);
            entity.Property(a => a.Details).HasColumnName("details").HasColumnType("jsonb");
            entity.Property(a => a.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            entity.Property(a => a.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<RemoteSessionRecording>(entity =>
        {
            entity.ToTable("remote_session_recordings");
            entity.HasKey(r => r.Id);

            entity.HasIndex(r => r.RemoteSessionId).IsUnique().HasDatabaseName("ix_remote_session_recordings_session_id");

            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.RemoteSessionId).HasColumnName("remote_session_id");
            entity.Property(r => r.StorageProvider).HasColumnName("storage_provider").HasConversion<int>();
            entity.Property(r => r.Status).HasColumnName("status").HasMaxLength(32);
            entity.Property(r => r.SourceCodec).HasColumnName("source_codec").HasConversion<int>();
            entity.Property(r => r.ContainerFormat).HasColumnName("container_format").HasMaxLength(16);
            entity.Property(r => r.StorageUrl).HasColumnName("storage_url").HasMaxLength(2048);
            entity.Property(r => r.Bytes).HasColumnName("bytes");
            entity.Property(r => r.DurationSec).HasColumnName("duration_sec");
            entity.Property(r => r.Resolution).HasColumnName("resolution").HasMaxLength(32);
            entity.Property(r => r.AverageFps).HasColumnName("average_fps");
            entity.Property(r => r.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            entity.Property(r => r.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamptz");
            entity.Property(r => r.StartedBy).HasColumnName("started_by").HasMaxLength(256);
            entity.Property(r => r.RetentionExpiresAt).HasColumnName("retention_expires_at").HasColumnType("timestamptz");
        });
    }
}

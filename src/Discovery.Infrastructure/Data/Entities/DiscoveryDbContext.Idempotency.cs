using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Idempotência de operações mutáveis (Idempotency-Key) ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureIdempotency(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("idempotency_records");
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.Scope, r.Key }).IsUnique().HasDatabaseName("ux_idempotency_scope_key");
            entity.HasIndex(r => r.ExpiresAt).HasDatabaseName("ix_idempotency_expires_at");

            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.Scope).HasColumnName("scope").HasMaxLength(150);
            entity.Property(r => r.Key).HasColumnName("key").HasMaxLength(200);
            entity.Property(r => r.Endpoint).HasColumnName("endpoint").HasMaxLength(300);
            entity.Property(r => r.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
            entity.Property(r => r.StatusCode).HasColumnName("status_code");
            entity.Property(r => r.ResponseBody).HasColumnName("response_body");
            entity.Property(r => r.ContentType).HasColumnName("content_type").HasMaxLength(150);
            entity.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(r => r.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");

            entity.Ignore(r => r.IsCompleted);
        });
    }
}

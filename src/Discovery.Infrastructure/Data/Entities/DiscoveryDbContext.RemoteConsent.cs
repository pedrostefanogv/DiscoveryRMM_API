using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Política de consentimento de acesso remoto por usuário/grupo ──

public partial class DiscoveryDbContext
{
    public DbSet<RemoteConsentPolicyOverride> RemoteConsentPolicyOverrides => Set<RemoteConsentPolicyOverride>();

    static partial void ConfigureRemoteConsent(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RemoteConsentPolicyOverride>(entity =>
        {
            entity.ToTable("remote_consent_policy_overrides");
            entity.HasKey(item => item.Id);

            entity.HasIndex(item => new { item.SubjectType, item.SubjectId, item.ClientId, item.SiteId })
                .IsUnique()
                .HasDatabaseName("ux_remote_consent_overrides_subject_scope");

            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(item => item.SubjectType).HasColumnName("subject_type").HasConversion<int>();
            entity.Property(item => item.SubjectId).HasColumnName("subject_id");
            entity.Property(item => item.ClientId).HasColumnName("client_id");
            entity.Property(item => item.SiteId).HasColumnName("site_id");
            entity.Property(item => item.Mode).HasColumnName("mode").HasConversion<int?>();
            entity.Property(item => item.TimeoutSeconds).HasColumnName("timeout_seconds");
            entity.Property(item => item.TimeoutAction).HasColumnName("timeout_action").HasConversion<int?>();
            entity.Property(item => item.ShowBanner).HasColumnName("show_banner");
            entity.Property(item => item.Message).HasColumnName("message").HasMaxLength(500);
            entity.Property(item => item.BannerText).HasColumnName("banner_text").HasMaxLength(500);
            entity.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(item => item.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
        });
    }
}

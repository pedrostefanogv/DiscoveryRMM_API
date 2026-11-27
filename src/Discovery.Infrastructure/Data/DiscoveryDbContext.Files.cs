using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

public partial class DiscoveryDbContext
{
    public DbSet<ManagedFile> ManagedFiles => Set<ManagedFile>();
    public DbSet<ManagedFileFolder> ManagedFileFolders => Set<ManagedFileFolder>();
    public DbSet<FileShareLink> FileShareLinks => Set<FileShareLink>();
    public DbSet<FileShareLinkItem> FileShareLinkItems => Set<FileShareLinkItem>();
    public DbSet<FileUploadLink> FileUploadLinks => Set<FileUploadLink>();
    public DbSet<FileAccessLog> FileAccessLogs => Set<FileAccessLog>();

    static partial void ConfigureFiles(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManagedFile>(entity =>
        {
            entity.ToTable("managed_files");
            entity.HasKey(f => f.Id);

            entity.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(f => f.Name).HasColumnName("name").HasMaxLength(255);
            entity.Property(f => f.FileName).HasColumnName("file_name").HasMaxLength(255);
            entity.Property(f => f.Description).HasColumnName("description").HasMaxLength(4000);
            entity.Property(f => f.Category).HasColumnName("category").HasMaxLength(100);
            entity.Property(f => f.FolderPath).HasColumnName("folder_path").HasMaxLength(500);
            entity.Property(f => f.TagsJson).HasColumnName("tags_json").HasColumnType("jsonb");
            entity.Property(f => f.Scope).HasColumnName("scope").HasConversion<short>();
            entity.Property(f => f.ClientId).HasColumnName("client_id");
            entity.Property(f => f.SiteId).HasColumnName("site_id");
            entity.Property(f => f.Visibility).HasColumnName("visibility").HasConversion<short>();
            entity.Property(f => f.Source).HasColumnName("source").HasConversion<short>();
            entity.Property(f => f.SourceUploadLinkId).HasColumnName("source_upload_link_id");
            entity.Property(f => f.StorageObjectKey).HasColumnName("storage_object_key").HasMaxLength(1024);
            entity.Property(f => f.StorageBucket).HasColumnName("storage_bucket").HasMaxLength(255);
            entity.Property(f => f.ContentType).HasColumnName("content_type").HasMaxLength(255);
            entity.Property(f => f.SizeBytes).HasColumnName("size_bytes");
            entity.Property(f => f.Sha256).HasColumnName("sha256").HasMaxLength(64);
            entity.Property(f => f.DownloadCount).HasColumnName("download_count");
            entity.Property(f => f.IsActive).HasColumnName("is_active");
            entity.Property(f => f.ExpiresAtUtc).HasColumnName("expires_at_utc");
            entity.Property(f => f.CreatedBy).HasColumnName("created_by").HasMaxLength(255);
            entity.Property(f => f.CreatedAt).HasColumnName("created_at");
            entity.Property(f => f.UpdatedAt).HasColumnName("updated_at");
            entity.Property(f => f.DeletedAt).HasColumnName("deleted_at");

            entity.HasIndex(f => f.StorageObjectKey).IsUnique();
        });

        modelBuilder.Entity<ManagedFileFolder>(entity =>
        {
            entity.ToTable("managed_file_folders");
            entity.HasKey(f => f.Id);

            entity.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(f => f.Name).HasColumnName("name").HasMaxLength(120);
            entity.Property(f => f.Path).HasColumnName("path").HasMaxLength(500);
            entity.Property(f => f.ParentPath).HasColumnName("parent_path").HasMaxLength(500);
            entity.Property(f => f.Description).HasColumnName("description").HasMaxLength(1000);
            entity.Property(f => f.Scope).HasColumnName("scope").HasConversion<short>();
            entity.Property(f => f.ClientId).HasColumnName("client_id");
            entity.Property(f => f.SiteId).HasColumnName("site_id");
            entity.Property(f => f.Visibility).HasColumnName("visibility").HasConversion<short>();
            entity.Property(f => f.CreatedBy).HasColumnName("created_by").HasMaxLength(255);
            entity.Property(f => f.CreatedAt).HasColumnName("created_at");
            entity.Property(f => f.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<FileShareLink>(entity =>
        {
            entity.ToTable("file_share_links");
            entity.HasKey(l => l.Id);

            entity.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(l => l.ShortCode).HasColumnName("short_code").HasMaxLength(32);
            entity.Property(l => l.Title).HasColumnName("title").HasMaxLength(255);
            entity.Property(l => l.Message).HasColumnName("message").HasMaxLength(4000);
            entity.Property(l => l.ClientId).HasColumnName("client_id");
            entity.Property(l => l.SiteId).HasColumnName("site_id");
            entity.Property(l => l.RequirePasscode).HasColumnName("require_passcode");
            entity.Property(l => l.PasscodeHash).HasColumnName("passcode_hash").HasMaxLength(512);
            entity.Property(l => l.FailedPasscodeAttempts).HasColumnName("failed_passcode_attempts");
            entity.Property(l => l.LockedUntilUtc).HasColumnName("locked_until_utc");
            entity.Property(l => l.ExpiresAtUtc).HasColumnName("expires_at_utc");
            entity.Property(l => l.MaxDownloads).HasColumnName("max_downloads");
            entity.Property(l => l.DownloadCount).HasColumnName("download_count");
            entity.Property(l => l.IsRevoked).HasColumnName("is_revoked");
            entity.Property(l => l.RevokedAtUtc).HasColumnName("revoked_at_utc");
            entity.Property(l => l.RevokedBy).HasColumnName("revoked_by").HasMaxLength(255);
            entity.Property(l => l.CreatedBy).HasColumnName("created_by").HasMaxLength(255);
            entity.Property(l => l.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(l => l.ShortCode).IsUnique();
        });

        modelBuilder.Entity<FileShareLinkItem>(entity =>
        {
            entity.ToTable("file_share_link_items");
            entity.HasKey(i => new { i.ShareLinkId, i.ManagedFileId });

            entity.Property(i => i.ShareLinkId).HasColumnName("share_link_id");
            entity.Property(i => i.ManagedFileId).HasColumnName("managed_file_id");

            entity.HasOne(i => i.ShareLink)
                .WithMany(l => l.Items)
                .HasForeignKey(i => i.ShareLinkId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(i => i.ManagedFile)
                .WithMany()
                .HasForeignKey(i => i.ManagedFileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FileUploadLink>(entity =>
        {
            entity.ToTable("file_upload_links");
            entity.HasKey(l => l.Id);

            entity.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(l => l.ShortCode).HasColumnName("short_code").HasMaxLength(32);
            entity.Property(l => l.Title).HasColumnName("title").HasMaxLength(255);
            entity.Property(l => l.Description).HasColumnName("description").HasMaxLength(4000);
            entity.Property(l => l.ClientId).HasColumnName("client_id");
            entity.Property(l => l.SiteId).HasColumnName("site_id");
            entity.Property(l => l.TicketId).HasColumnName("ticket_id");
            entity.Property(l => l.TargetFolderPath).HasColumnName("target_folder_path").HasMaxLength(500);
            entity.Property(l => l.RequirePasscode).HasColumnName("require_passcode");
            entity.Property(l => l.PasscodeHash).HasColumnName("passcode_hash").HasMaxLength(512);
            entity.Property(l => l.FailedPasscodeAttempts).HasColumnName("failed_passcode_attempts");
            entity.Property(l => l.LockedUntilUtc).HasColumnName("locked_until_utc");
            entity.Property(l => l.ExpiresAtUtc).HasColumnName("expires_at_utc");
            entity.Property(l => l.AllowMultiple).HasColumnName("allow_multiple");
            entity.Property(l => l.MaxFiles).HasColumnName("max_files");
            entity.Property(l => l.UploadedFilesCount).HasColumnName("uploaded_files_count");
            entity.Property(l => l.MaxFileSizeBytes).HasColumnName("max_file_size_bytes");
            entity.Property(l => l.MaxTotalSizeBytes).HasColumnName("max_total_size_bytes");
            entity.Property(l => l.UploadedTotalBytes).HasColumnName("uploaded_total_bytes");
            entity.Property(l => l.AllowedMimeTypesJson).HasColumnName("allowed_mime_types_json").HasColumnType("jsonb");
            entity.Property(l => l.AllowedExtensionsJson).HasColumnName("allowed_extensions_json").HasColumnType("jsonb");
            entity.Property(l => l.FileRetentionDays).HasColumnName("file_retention_days");
            entity.Property(l => l.NotifyOnUpload).HasColumnName("notify_on_upload");
            entity.Property(l => l.IsRevoked).HasColumnName("is_revoked");
            entity.Property(l => l.RevokedAtUtc).HasColumnName("revoked_at_utc");
            entity.Property(l => l.RevokedBy).HasColumnName("revoked_by").HasMaxLength(255);
            entity.Property(l => l.CreatedBy).HasColumnName("created_by").HasMaxLength(255);
            entity.Property(l => l.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(l => l.ShortCode).IsUnique();
        });

        modelBuilder.Entity<FileAccessLog>(entity =>
        {
            entity.ToTable("file_access_logs");
            entity.HasKey(a => a.Id);

            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.EventType).HasColumnName("event_type").HasMaxLength(50);
            entity.Property(a => a.ManagedFileId).HasColumnName("managed_file_id");
            entity.Property(a => a.ShareLinkId).HasColumnName("share_link_id");
            entity.Property(a => a.UploadLinkId).HasColumnName("upload_link_id");
            entity.Property(a => a.AgentId).HasColumnName("agent_id");
            entity.Property(a => a.ClientIp).HasColumnName("client_ip").HasMaxLength(64);
            entity.Property(a => a.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
            entity.Property(a => a.DetailsJson).HasColumnName("details_json").HasColumnType("jsonb");
            entity.Property(a => a.CreatedAt).HasColumnName("created_at");
        });
    }
}

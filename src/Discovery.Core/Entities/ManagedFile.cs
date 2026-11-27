using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Arquivo gerenciado no Object Storage (S3/RustFS/MinIO) para distribuição
/// pública (/download), links temporários e aba Arquivos dos agents (Global → Client → Site).
/// </summary>
public class ManagedFile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string FolderPath { get; set; } = "/";
    public string TagsJson { get; set; } = "[]";

    public FileScope Scope { get; set; } = FileScope.Global;
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }

    public FileVisibility Visibility { get; set; } = FileVisibility.Private;
    public FileSource Source { get; set; } = FileSource.AdminUpload;
    public Guid? SourceUploadLinkId { get; set; }

    public string StorageObjectKey { get; set; } = string.Empty;
    public string StorageBucket { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }

    public long DownloadCount { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? ExpiresAtUtc { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
}

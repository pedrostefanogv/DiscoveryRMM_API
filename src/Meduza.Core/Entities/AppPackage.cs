using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

public class AppPackage
{
    public Guid Id { get; set; }
    public AppInstallationType InstallationType { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? IconUrl { get; set; }
    public string? SiteUrl { get; set; }
    public string? InstallCommand { get; set; }
    public string? MetadataJson { get; set; }
    public string? FileObjectKey { get; set; }
    public string? FileBucket { get; set; }
    public string? FilePublicUrl { get; set; }
    public string? FileContentType { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? FileChecksum { get; set; }
    public DateTime? SourceGeneratedAt { get; set; }
    public DateTime? LastUpdated { get; set; }
    public DateTime SyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

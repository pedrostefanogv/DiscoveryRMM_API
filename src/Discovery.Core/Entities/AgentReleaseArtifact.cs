using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AgentReleaseArtifact
{
    public Guid Id { get; set; }
    public Guid AgentReleaseId { get; set; }
    public string Platform { get; set; } = "windows";
    public string Architecture { get; set; } = "amd64";
    public AgentReleaseArtifactType ArtifactType { get; set; } = AgentReleaseArtifactType.Portable;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public string StorageObjectKey { get; set; } = string.Empty;
    public string StorageBucket { get; set; } = string.Empty;
    public int StorageProviderType { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? SignatureThumbprint { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AgentRelease? Release { get; set; }
}

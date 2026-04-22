namespace Discovery.Core.Entities;

public class AgentRelease
{
    public Guid Id { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Channel { get; set; } = "stable";
    public bool IsActive { get; set; } = true;
    public bool Mandatory { get; set; } = false;
    public string? MinimumSupportedVersion { get; set; }
    public string? ReleaseNotes { get; set; }
    public DateTime PublishedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    public List<AgentReleaseArtifact> Artifacts { get; set; } = [];
}

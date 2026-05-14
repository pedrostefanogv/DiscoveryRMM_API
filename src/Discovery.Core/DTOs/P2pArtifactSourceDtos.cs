namespace Discovery.Core.DTOs;

/// <summary>
/// Response da URL canônica de um artifact P2P.
/// </summary>
public class P2pArtifactSourceResponse
{
    public Guid ArtifactId { get; set; }
    public string ArtifactName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Source { get; set; } = string.Empty;
    public string ExpiresAtUtc { get; set; } = string.Empty;
}

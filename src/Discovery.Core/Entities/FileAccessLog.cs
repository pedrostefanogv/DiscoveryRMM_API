namespace Discovery.Core.Entities;

/// <summary>Registro de auditoria de downloads, uploads e verificações de contra-senha.</summary>
public class FileAccessLog
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Guid? ManagedFileId { get; set; }
    public Guid? ShareLinkId { get; set; }
    public Guid? UploadLinkId { get; set; }
    public Guid? AgentId { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public string? DetailsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

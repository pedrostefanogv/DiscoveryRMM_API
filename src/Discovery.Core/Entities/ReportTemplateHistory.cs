namespace Discovery.Core.Entities;

public class ReportTemplateHistory
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public int Version { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = "{}";
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
}

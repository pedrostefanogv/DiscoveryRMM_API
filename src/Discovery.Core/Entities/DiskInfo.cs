namespace Discovery.Core.Entities;

public class DiskInfo
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string DriveLetter { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public long TotalSizeBytes { get; set; }
    public long FreeSpaceBytes { get; set; }
    public string? MediaType { get; set; }
    public DateTime CollectedAt { get; set; }
}

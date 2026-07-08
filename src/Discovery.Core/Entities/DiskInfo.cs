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

    // ── Report field aliases ────────────────────────────────────────────
    public string Name => DriveLetter;
    public long SizeBytes => TotalSizeBytes;
    public long FreeBytes => FreeSpaceBytes;
    public string? Interface => MediaType;
    public string? Type => MediaType;
    public string? SerialNumber => null;
    public string? HealthStatus => null;
}

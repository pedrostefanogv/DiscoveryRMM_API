namespace Discovery.Core.Entities;

/// <summary>Metadados do último scan de Windows Update de um agente.</summary>
public class AgentWindowsUpdateSnapshot
{
    public Guid AgentId { get; set; }
    public DateTime? CollectedAt { get; set; }
    public string ScanStatus { get; set; } = "ok";
    public int? ScanDurationMs { get; set; }
    public string? ScanError { get; set; }
    public int PendingCount { get; set; }
    public int InstalledCount { get; set; }
    public int DeniedCount { get; set; }
    public bool PendingReboot { get; set; }
    public string? PendingRebootReasons { get; set; }
    public DateTime? RebootCheckedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

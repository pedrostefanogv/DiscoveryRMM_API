using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class ReportTemplate
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Instructions { get; set; }
    public string? ExecutionSchemaJson { get; set; }
    public ReportDatasetType DatasetType { get; set; }
    public ReportFormat DefaultFormat { get; set; } = ReportFormat.Xlsx;
    public string LayoutJson { get; set; } = "{}";
    public string? FiltersJson { get; set; }
    public bool IsActive { get; set; } = true;
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
}

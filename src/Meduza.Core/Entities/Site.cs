namespace Meduza.Core.Entities;

public class Site
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class LogEntry
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    public LogType Type { get; set; }
    public LogLevel Level { get; set; }
    public LogSource Source { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? DataJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

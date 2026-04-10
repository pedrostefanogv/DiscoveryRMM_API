using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class LogQuery
{
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    public LogType? Type { get; set; }
    public LogLevel? Level { get; set; }
    public LogSource? Source { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Limit { get; set; } = 100;
    public int Offset { get; set; } = 0;
}

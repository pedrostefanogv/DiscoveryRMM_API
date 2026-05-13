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
    public string? SearchText { get; set; }
    public string? TraceId { get; set; }
    public string? CorrelationId { get; set; }
    public string? RequestPath { get; set; }
    public int? StatusCode { get; set; }
    public string? PeriodPreset { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public DateTime? CursorCreatedAtUtc { get; set; }
    public Guid? CursorId { get; set; }
    public bool HasGlobalAccess { get; set; }
    public IReadOnlyList<Guid> AllowedClientIds { get; set; } = [];
    public IReadOnlyList<Guid> AllowedSiteIds { get; set; } = [];
    public int Limit { get; set; } = 100;
    public int Offset { get; set; } = 0;
}

using Discovery.Core.Entities;
using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

public interface ILogRepository
{
    Task<LogEntry> CreateAsync(LogEntry entry);
    Task<IEnumerable<LogEntry>> QueryAsync(LogQuery query);
    Task<IReadOnlyList<LogEntry>> QueryPageAsync(LogQuery query);
    Task<LogSummaryRawDto> GetSummaryAsync(LogQuery query);
    Task<int> PurgeAsync(DateTime cutoff);
}

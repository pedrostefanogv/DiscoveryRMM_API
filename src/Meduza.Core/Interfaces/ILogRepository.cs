using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface ILogRepository
{
    Task<LogEntry> CreateAsync(LogEntry entry);
    Task<IEnumerable<LogEntry>> QueryAsync(LogQuery query);
    Task<int> PurgeAsync(DateTime cutoff);
}

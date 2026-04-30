using Discovery.Core.Entities.Security;

namespace Discovery.Core.Interfaces.Security;

public interface IAuthAuditLogRepository
{
    Task AddAsync(AuthAuditLog log);
    Task<IEnumerable<AuthAuditLog>> GetByUserAsync(Guid userId, int limit = 100);
    Task<IEnumerable<AuthAuditLog>> GetRecentAsync(int limit = 200);
    Task<IEnumerable<AuthAuditLog>> GetFailedAsync(DateTime since, int limit = 100);
}

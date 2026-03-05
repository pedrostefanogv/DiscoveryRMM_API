using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;

namespace Meduza.Infrastructure.Services;

public class ConfigurationAuditService : IConfigurationAuditService
{
    private readonly IConfigurationAuditRepository _repo;

    public ConfigurationAuditService(IConfigurationAuditRepository repo) => _repo = repo;

    public async Task LogChangeAsync(string entityType, Guid entityId, string fieldName,
        string? oldValue, string? newValue, string? reason = null,
        string? changedBy = null, string? ipAddress = null)
    {
        if (!Enum.TryParse<ConfigurationEntityType>(entityType, ignoreCase: true, out var entityTypeEnum))
            entityTypeEnum = ConfigurationEntityType.Server;

        await _repo.CreateAsync(new ConfigurationAudit
        {
            EntityType = entityTypeEnum,
            EntityId = entityId,
            FieldName = fieldName,
            OldValue = oldValue,
            NewValue = newValue,
            Reason = reason,
            ChangedBy = changedBy,
            IpAddress = ipAddress
        });
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetEntityHistoryAsync(string entityType, Guid entityId, int limit = 100)
        => await _repo.GetByEntityAsync(entityType, entityId, limit);

    public async Task<IEnumerable<ConfigurationAudit>> GetRecentChangesAsync(int days = 90, int limit = 1000)
        => await _repo.GetRecentAsync(days, limit);

    public async Task<IEnumerable<ConfigurationAudit>> GetChangesByUserAsync(string username, int limit = 100)
        => await _repo.GetByUserAsync(username, limit);

    public async Task<IEnumerable<ConfigurationAudit>> GetFieldHistoryAsync(string entityType, Guid entityId, string fieldName)
    {
        var all = await _repo.GetByEntityAsync(entityType, entityId, 1000);
        return all.Where(a => a.FieldName.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetAuditReportAsync(DateTime startDate, DateTime endDate)
    {
        var days = (int)Math.Ceiling((DateTime.UtcNow - startDate).TotalDays);
        var all = await _repo.GetRecentAsync(days, 10000);
        return all.Where(a => a.ChangedAt >= startDate && a.ChangedAt <= endDate);
    }
}

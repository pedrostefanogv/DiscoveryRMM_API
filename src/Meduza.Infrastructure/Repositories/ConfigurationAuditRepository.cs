using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class ConfigurationAuditRepository : IConfigurationAuditRepository
{
    private readonly IDbConnectionFactory _db;

    public ConfigurationAuditRepository(IDbConnectionFactory db) => _db = db;

    public async Task CreateAsync(ConfigurationAudit audit)
    {
        audit.Id = IdGenerator.NewId();
        audit.ChangedAt = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO configuration_audits (
                id, entity_type, entity_id, field_name,
                old_value, new_value, reason, changed_by,
                changed_at, ip_address, entity_version
            ) VALUES (
                @Id, @EntityType, @EntityId, @FieldName,
                @OldValue, @NewValue, @Reason, @ChangedBy,
                @ChangedAt, @IpAddress, @EntityVersion
            )
            """,
            new
            {
                audit.Id,
                EntityType = audit.EntityType.ToString(),
                audit.EntityId,
                audit.FieldName,
                audit.OldValue,
                audit.NewValue,
                audit.Reason,
                audit.ChangedBy,
                audit.ChangedAt,
                audit.IpAddress,
                audit.EntityVersion
            });
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetByEntityAsync(string entityType, Guid entityId, int limit = 100)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<ConfigurationAudit>(
            """
            SELECT id, entity_type AS EntityType, entity_id AS EntityId, field_name AS FieldName,
                   old_value AS OldValue, new_value AS NewValue, reason, changed_by AS ChangedBy,
                   changed_at AS ChangedAt, ip_address AS IpAddress, entity_version AS EntityVersion
            FROM configuration_audits
            WHERE entity_type = @EntityType AND entity_id = @EntityId
            ORDER BY changed_at DESC
            LIMIT @Limit
            """,
            new { EntityType = entityType, EntityId = entityId, Limit = limit });
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetRecentAsync(int days = 90, int limit = 1000)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<ConfigurationAudit>(
            """
            SELECT id, entity_type AS EntityType, entity_id AS EntityId, field_name AS FieldName,
                   old_value AS OldValue, new_value AS NewValue, reason, changed_by AS ChangedBy,
                   changed_at AS ChangedAt, ip_address AS IpAddress, entity_version AS EntityVersion
            FROM configuration_audits
            WHERE changed_at >= @Since
            ORDER BY changed_at DESC
            LIMIT @Limit
            """,
            new { Since = DateTime.UtcNow.AddDays(-days), Limit = limit });
    }

    public async Task<IEnumerable<ConfigurationAudit>> GetByUserAsync(string username, int limit = 100)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<ConfigurationAudit>(
            """
            SELECT id, entity_type AS EntityType, entity_id AS EntityId, field_name AS FieldName,
                   old_value AS OldValue, new_value AS NewValue, reason, changed_by AS ChangedBy,
                   changed_at AS ChangedAt, ip_address AS IpAddress, entity_version AS EntityVersion
            FROM configuration_audits
            WHERE changed_by = @Username
            ORDER BY changed_at DESC
            LIMIT @Limit
            """,
            new { Username = username, Limit = limit });
    }
}

using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Npgsql;

namespace Meduza.Infrastructure.Repositories;

public class AgentRepository : IAgentRepository
{
    private readonly IDbConnectionFactory _db;

    public AgentRepository(IDbConnectionFactory db) => _db = db;

    public async Task<Agent?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<Agent>(
            """
            SELECT id, site_id AS SiteId, hostname, display_name AS DisplayName, status,
                   operating_system AS OperatingSystem, os_version AS OsVersion,
                   agent_version AS AgentVersion, last_ip_address AS LastIpAddress,
                   mac_address AS MacAddress, last_seen_at AS LastSeenAt,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM agents WHERE id = @Id
            """, new { Id = id });
    }

    public async Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<Agent>(
            """
            SELECT id, site_id AS SiteId, hostname, display_name AS DisplayName, status,
                   operating_system AS OperatingSystem, os_version AS OsVersion,
                   agent_version AS AgentVersion, last_ip_address AS LastIpAddress,
                   mac_address AS MacAddress, last_seen_at AS LastSeenAt,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM agents WHERE site_id = @SiteId ORDER BY hostname
            """, new { SiteId = siteId });
    }

    public async Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryAsync<Agent>(
            """
            SELECT a.id, a.site_id AS SiteId, a.hostname, a.display_name AS DisplayName, a.status,
                   a.operating_system AS OperatingSystem, a.os_version AS OsVersion,
                   a.agent_version AS AgentVersion, a.last_ip_address AS LastIpAddress,
                   a.mac_address AS MacAddress, a.last_seen_at AS LastSeenAt,
                   a.created_at AS CreatedAt, a.updated_at AS UpdatedAt
            FROM agents a
            INNER JOIN sites s ON s.id = a.site_id
            WHERE s.client_id = @ClientId ORDER BY a.hostname
            """, new { ClientId = clientId });
    }

    public async Task<Agent> CreateAsync(Agent agent)
    {
        agent.Id = IdGenerator.NewId();
        agent.CreatedAt = DateTime.UtcNow;
        agent.UpdatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO agents (id, site_id, hostname, display_name, status, operating_system, os_version,
                   agent_version, last_ip_address, mac_address, last_seen_at, created_at, updated_at)
            VALUES (@Id, @SiteId, @Hostname, @DisplayName, @Status, @OperatingSystem, @OsVersion,
                   @AgentVersion, @LastIpAddress, @MacAddress, @LastSeenAt, @CreatedAt, @UpdatedAt)
            """, agent);
        return agent;
    }

    public async Task UpdateAsync(Agent agent)
    {
        agent.UpdatedAt = DateTime.UtcNow;
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE agents SET site_id = @SiteId, hostname = @Hostname, display_name = @DisplayName,
                   status = @Status, operating_system = @OperatingSystem, os_version = @OsVersion,
                   agent_version = @AgentVersion, last_ip_address = @LastIpAddress,
                   mac_address = @MacAddress, last_seen_at = @LastSeenAt, updated_at = @UpdatedAt
            WHERE id = @Id
            """, agent);
    }

    public async Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress)
    {
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var conn = _db.CreateConnection();
                await conn.ExecuteAsync(
                    """
                    UPDATE agents SET status = @Status, last_ip_address = @IpAddress,
                           last_seen_at = @Now, updated_at = @Now
                    WHERE id = @Id
                    """, new { Id = id, Status = (int)status, IpAddress = ipAddress, Now = DateTime.UtcNow });
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                await Task.Delay(150);
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is TimeoutException)
            return true;

        if (ex is NpgsqlException npgsqlEx)
            return npgsqlEx.IsTransient || npgsqlEx.InnerException is TimeoutException;

        return false;
    }

    public async Task DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM agents WHERE id = @Id", new { Id = id });
    }
}

using System.Text;
using Dapper;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;

namespace Meduza.Infrastructure.Repositories;

public class LogRepository : ILogRepository
{
    private readonly IDbConnectionFactory _db;

    public LogRepository(IDbConnectionFactory db) => _db = db;

    public async Task<LogEntry> CreateAsync(LogEntry entry)
    {
        entry.Id = IdGenerator.NewId();
        entry.CreatedAt = DateTime.UtcNow;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO logs (id, client_id, site_id, agent_id, log_type, log_level, log_source,
                   message, data_json, created_at)
            VALUES (@Id, @ClientId, @SiteId, @AgentId, @Type, @Level, @Source,
                   @Message, @DataJson::jsonb, @CreatedAt)
            """, entry);

        return entry;
    }

    public async Task<IEnumerable<LogEntry>> QueryAsync(LogQuery query)
    {
        using var conn = _db.CreateConnection();

        var sql = new StringBuilder(
            """
            SELECT id, client_id AS ClientId, site_id AS SiteId, agent_id AS AgentId,
                   log_type AS Type, log_level AS Level, log_source AS Source,
                   message, data_json AS DataJson, created_at AS CreatedAt
            FROM logs
            """);

        var where = new List<string>();
        var parameters = new DynamicParameters();

        if (query.ClientId.HasValue)
        {
            where.Add("client_id = @ClientId");
            parameters.Add("ClientId", query.ClientId);
        }
        if (query.SiteId.HasValue)
        {
            where.Add("site_id = @SiteId");
            parameters.Add("SiteId", query.SiteId);
        }
        if (query.AgentId.HasValue)
        {
            where.Add("agent_id = @AgentId");
            parameters.Add("AgentId", query.AgentId);
        }
        if (query.Type.HasValue)
        {
            where.Add("log_type = @Type");
            parameters.Add("Type", query.Type);
        }
        if (query.Level.HasValue)
        {
            where.Add("log_level = @Level");
            parameters.Add("Level", query.Level);
        }
        if (query.Source.HasValue)
        {
            where.Add("log_source = @Source");
            parameters.Add("Source", query.Source);
        }
        if (query.From.HasValue)
        {
            where.Add("created_at >= @From");
            parameters.Add("From", query.From);
        }
        if (query.To.HasValue)
        {
            where.Add("created_at <= @To");
            parameters.Add("To", query.To);
        }

        if (where.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", where));

        var limit = Math.Clamp(query.Limit, 1, 500);
        var offset = query.Offset < 0 ? 0 : query.Offset;
        parameters.Add("Limit", limit);
        parameters.Add("Offset", offset);

        sql.Append(" ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset");

        return await conn.QueryAsync<LogEntry>(sql.ToString(), parameters);
    }

    public async Task<int> PurgeAsync(DateTime cutoff)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteAsync("DELETE FROM logs WHERE created_at < @Cutoff", new { Cutoff = cutoff });
    }
}

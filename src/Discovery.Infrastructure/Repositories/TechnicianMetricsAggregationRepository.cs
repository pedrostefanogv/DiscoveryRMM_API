using System.Data;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Discovery.Infrastructure.Repositories;

/// <summary>
/// Agregação das métricas por atendente em SQL. Substitui o carregamento de
/// todos os tickets do lote em memória: o resultado é uma linha por atendente.
///
/// Observação: SQL específico de PostgreSQL (FILTER, percentile_cont) — é o
/// provider do produto. Falha aqui não derruba o ciclo: o chamador registra e
/// mantém o snapshot anterior.
/// </summary>
public class TechnicianMetricsAggregationRepository(
    DiscoveryDbContext db,
    ILogger<TechnicianMetricsAggregationRepository> logger) : ITechnicianMetricsAggregationRepository
{
    private const string AggregatesSql = @"
        SELECT assigned_to_user_id AS user_id,
               COUNT(*) FILTER (WHERE created_at >= @since)                          AS assigned_total,
               COUNT(*) FILTER (WHERE closed_at IS NOT NULL AND created_at >= @since) AS resolved_total,
               COUNT(*) FILTER (WHERE closed_at IS NULL)                              AS open_now,
               AVG(EXTRACT(EPOCH FROM (first_responded_at - created_at)) / 60.0)
                   FILTER (WHERE first_responded_at IS NOT NULL
                             AND first_responded_at >= created_at
                             AND created_at >= @since)                                 AS avg_frt_minutes,
               AVG(EXTRACT(EPOCH FROM (closed_at - created_at)) / 60.0)
                   FILTER (WHERE closed_at IS NOT NULL
                             AND closed_at >= created_at
                             AND created_at >= @since)                                 AS avg_resolution_minutes,
               percentile_cont(0.90) WITHIN GROUP (
                   ORDER BY EXTRACT(EPOCH FROM (closed_at - created_at)) / 60.0)
                   FILTER (WHERE closed_at IS NOT NULL
                             AND closed_at >= created_at
                             AND created_at >= @since)                                 AS p90_resolution_minutes,
               COUNT(*) FILTER (WHERE sla_breached AND created_at >= @since)          AS sla_breached,
               AVG(rating)::float8
                   FILTER (WHERE rating IS NOT NULL AND created_at >= @since)         AS csat_average,
               COUNT(rating) FILTER (WHERE created_at >= @since)                      AS csat_rated_count
        FROM tickets
        WHERE deleted_at IS NULL
          AND assigned_to_user_id = ANY(@user_ids)
          AND (created_at >= @since OR closed_at IS NULL)
        GROUP BY assigned_to_user_id;";

    private const string TopCategoriesSql = @"
        SELECT user_id, category, cnt FROM (
            SELECT t.assigned_to_user_id AS user_id,
                   t.category,
                   COUNT(*) AS cnt,
                   ROW_NUMBER() OVER (PARTITION BY t.assigned_to_user_id
                                      ORDER BY COUNT(*) DESC, t.category) AS rn
            FROM tickets t
            WHERE t.deleted_at IS NULL
              AND t.assigned_to_user_id = ANY(@user_ids)
              AND t.created_at >= @since
              AND t.category IS NOT NULL
              AND t.category <> ''
            GROUP BY t.assigned_to_user_id, t.category
        ) ranked
        WHERE rn <= @per_user
        ORDER BY user_id, rn;";

    private const string ReopensSql = @"
        SELECT t.assigned_to_user_id AS user_id,
               COUNT(DISTINCT l.ticket_id) AS cnt
        FROM ticket_activity_logs l
        JOIN tickets t ON t.id = l.ticket_id
        WHERE l.activity_type = 'Reopened'
          AND l.created_at >= @since
          AND t.deleted_at IS NULL
          AND t.assigned_to_user_id = ANY(@user_ids)
        GROUP BY t.assigned_to_user_id;";

    private const string DifficultySql = @"
        SELECT chosen_user_id AS user_id,
               AVG(difficulty)::float8 AS avg_difficulty
        FROM ticket_assignment_decisions
        WHERE applied = true
          AND chosen_user_id = ANY(@user_ids)
          AND created_at >= @since
        GROUP BY chosen_user_id;";

    public async Task<IReadOnlyList<TechnicianMetricsAggregateRow>> GetAggregatesAsync(
        IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return [];

        var rows = new List<TechnicianMetricsAggregateRow>();
        await ExecuteAsync(AggregatesSql, ids, since, ct, reader =>
        {
            rows.Add(new TechnicianMetricsAggregateRow(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.GetInt32(9)));
        });

        return rows;
    }

    public async Task<IReadOnlyList<TechnicianCategoryCount>> GetTopCategoriesAsync(
        IReadOnlyCollection<Guid> userIds, DateTime since, int perUser, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return [];

        var rows = new List<TechnicianCategoryCount>();
        await ExecuteAsync(TopCategoriesSql, ids, since, ct, reader =>
        {
            rows.Add(new TechnicianCategoryCount(
                reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2)));
        }, extra: command => command.Parameters.Add(new NpgsqlParameter("per_user", Math.Clamp(perUser, 1, 20))));

        return rows;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetReopenCountsAsync(
        IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, int>();

        var result = new Dictionary<Guid, int>();
        await ExecuteAsync(ReopensSql, ids, since, ct, reader =>
        {
            result[reader.GetGuid(0)] = reader.GetInt32(1);
        });

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, double>> GetDifficultyAveragesAsync(
        IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, double>();

        var result = new Dictionary<Guid, double>();
        await ExecuteAsync(DifficultySql, ids, since, ct, reader =>
        {
            result[reader.GetGuid(0)] = reader.GetDouble(1);
        });

        return result;
    }

    private async Task ExecuteAsync(
        string sql, Guid[] userIds, DateTime since, CancellationToken ct,
        Action<System.Data.Common.DbDataReader> read,
        Action<System.Data.Common.DbCommand>? extra = null)
    {
        try
        {
            var connection = db.Database.GetDbConnection();
            var openedHere = connection.State != ConnectionState.Open;
            if (openedHere) await connection.OpenAsync(ct);

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.Add(new NpgsqlParameter("user_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    Value = userIds
                });
                command.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz)
                {
                    Value = since
                });
                extra?.Invoke(command);

                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    read(reader);
            }
            finally
            {
                if (openedHere) await connection.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha na agregação de métricas por atendente (SQL).");
            throw;
        }
    }
}

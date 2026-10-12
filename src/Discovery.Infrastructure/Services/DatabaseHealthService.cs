using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Diagnóstico físico do PostgreSQL por catálogo (somente leitura): índices
/// inválidos, bloat, índices não usados, FKs sem índice, estatísticas velhas,
/// idade de XID (wraparound) e transações longas que bloqueiam o vacuum.
/// Cada consulta é isolada — uma falha de permissão não derruba o relatório.
/// </summary>
public sealed class DatabaseHealthService(
    DiscoveryDbContext db,
    ILogger<DatabaseHealthService> logger) : IDatabaseHealthService
{
    private const int MaxSamples = 20;
    private const long UnusedIndexMinBytes = 1_048_576;      // 1 MiB
    private const long BloatMinDeadTuples = 1_000;           // ignora tabelas minúsculas
    private const double BloatMinPercent = 20d;
    private static readonly TimeSpan StaleStatsAfter = TimeSpan.FromDays(7);
    private static readonly TimeSpan LongTransactionAfter = TimeSpan.FromMinutes(5);

    public async Task<DatabaseHealthReport> GetReportAsync(CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        var generatedAt = DateTime.UtcNow;
        var databaseSize = await ScalarLongAsync(connection, "SELECT pg_database_size(current_database())", ct) ?? 0;
        var freezeMaxAge = await ScalarLongAsync(connection,
            "SELECT setting::bigint FROM pg_settings WHERE name = 'autovacuum_freeze_max_age'", ct) ?? 200_000_000L;
        var xidWarnThreshold = (long)(freezeMaxAge * 0.5d);

        var tables = await QueryAsync(connection, """
            SELECT c.relname,
                   pg_total_relation_size(c.oid)                              AS total_bytes,
                   COALESCE(s.n_live_tup, 0)                                  AS live_tuples,
                   COALESCE(s.n_dead_tup, 0)                                  AS dead_tuples,
                   s.last_vacuum, s.last_autovacuum, s.last_analyze,
                   age(c.relfrozenxid)                                        AS xid_age
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'm')
            ORDER BY total_bytes DESC
            """, reader => new DatabaseTableStat(
                reader.GetString(0),
                reader.GetInt64(1),
                Convert.ToInt64(reader.GetValue(2)),
                Convert.ToInt64(reader.GetValue(3)),
                DeadPercent(Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3))),
                ReadUtc(reader, 4),
                ReadUtc(reader, 5),
                ReadUtc(reader, 6),
                Convert.ToInt64(reader.GetValue(7))), ct);

        var issues = new List<DatabaseHealthIssue>();

        // Índices inválidos — integridade (sempre reparáveis pelo job).
        var invalidIndexes = await QueryAsync(connection, """
            SELECT ic.relname AS index_name, tc.relname AS table_name
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_class tc ON tc.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = ic.relnamespace
            WHERE NOT i.indisvalid AND n.nspname = 'public'
            ORDER BY tc.relname, ic.relname
            """, reader => new { Index = reader.GetString(0), Table = reader.GetString(1) }, ct);
        issues.Add(new DatabaseHealthIssue(
            DatabaseHealthIssueKeys.InvalidIndexes,
            "Índices inválidos",
            "critical",
            invalidIndexes.Count,
            invalidIndexes.Take(MaxSamples).Select(i => $"{i.Table}.{i.Index}").ToList()));

        // Bloat: tuplas mortas em relação às vivas.
        var bloated = tables
            .Where(t => t.DeadTuples >= BloatMinDeadTuples && t.DeadPercent >= BloatMinPercent)
            .ToList();
        issues.Add(new DatabaseHealthIssue(
            DatabaseHealthIssueKeys.BloatedTables,
            "Tabelas com bloat (candidatas a VACUUM)",
            "warning",
            bloated.Count,
            bloated.Take(MaxSamples).Select(t => $"{t.Table} — {t.DeadPercent:0.#}% mortas ({t.DeadTuples} de {t.LiveTuples})").ToList()));

        // Estatísticas velhas.
        var stale = tables
            .Where(t => t.LiveTuples > 0
                && (t.LastAnalyzeUtc is null || generatedAt - t.LastAnalyzeUtc.Value > StaleStatsAfter))
            .ToList();
        issues.Add(new DatabaseHealthIssue(
            DatabaseHealthIssueKeys.StaleStatistics,
            "Estatísticas desatualizadas (ANALYZE)",
            "warning",
            stale.Count,
            stale.Take(MaxSamples).Select(t => $"{t.Table} — último ANALYZE: {(t.LastAnalyzeUtc?.ToString("u") ?? "nunca")}").ToList()));

        // Nunca vacuumadas (autovacuum pode estar desligado/atrasado).
        var neverVacuumed = tables
            .Where(t => t.LastVacuumUtc is null && t.LastAutovacuumUtc is null)
            .ToList();
        issues.Add(new DatabaseHealthIssue(
            DatabaseHealthIssueKeys.NeverVacuumed,
            "Tabelas nunca vacuumadas",
            "warning",
            neverVacuumed.Count,
            neverVacuumed.Take(MaxSamples).Select(t => t.Table).ToList()));

        // Idade de XID (wraparound).
        var aged = tables.Where(t => t.XidAge >= xidWarnThreshold).OrderByDescending(t => t.XidAge).ToList();
        issues.Add(new DatabaseHealthIssue(
            DatabaseHealthIssueKeys.XidAge,
            "Idade de XID elevada (wraparound)",
            "critical",
            aged.Count,
            aged.Take(MaxSamples).Select(t => $"{t.Table} — age(relfrozenxid)={t.XidAge:N0} (limite {freezeMaxAge:N0})").ToList()));

        // Índices não usados (relatório; nunca remover automaticamente).
        var unusedIndexes = await QueryAsync(connection, """
            SELECT s.relname AS table_name, s.indexrelname, s.idx_scan,
                   pg_relation_size(s.indexrelid) AS bytes
            FROM pg_stat_user_indexes s
            WHERE s.idx_scan = 0
              AND s.indexrelid NOT IN (SELECT conindid FROM pg_constraint WHERE contype IN ('p', 'u'))
              AND pg_relation_size(s.indexrelid) >= 1048576
            ORDER BY pg_relation_size(s.indexrelid) DESC
            LIMIT 50
            """, reader => new
            {
                Table = reader.GetString(0),
                Index = reader.GetString(1),
                Bytes = reader.GetInt64(3),
            }, ct);
        issues.Add(new DatabaseHealthIssue(
            DatabaseHealthIssueKeys.UnusedIndexes,
            "Índices sem uso",
            "info",
            unusedIndexes.Count,
            unusedIndexes.Take(MaxSamples).Select(i => $"{i.Table}.{i.Index} — {FormatBytes(i.Bytes)}").ToList()));

        // FKs sem índice correspondente.
        var fkWithoutIndex = await QueryAsync(connection, """
            SELECT con.conrelid::regclass::text AS table_name, con.conname
            FROM pg_constraint con
            JOIN pg_namespace n ON n.oid = con.connamespace
            WHERE con.contype = 'f' AND n.nspname = 'public'
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_index i
                  WHERE i.indrelid = con.conrelid
                    AND i.indisvalid
                    AND i.indkey[0] = con.conkey[1]
              )
            ORDER BY 1, 2
            LIMIT 50
            """, reader => new { Table = reader.GetString(0), Constraint = reader.GetString(1) }, ct);
        issues.Add(new DatabaseHealthIssue(
            DatabaseHealthIssueKeys.ForeignKeysWithoutIndex,
            "Foreign keys sem índice",
            "info",
            fkWithoutIndex.Count,
            fkWithoutIndex.Take(MaxSamples).Select(f => $"{f.Table}.{f.Constraint}").ToList()));

        // Transações longas bloqueando o vacuum.
        var longTransactions = await QueryAsync(connection, $"""
            SELECT pid, COALESCE(state, 'unknown'), now() - xact_start AS age
            FROM pg_stat_activity
            WHERE xact_start IS NOT NULL
              AND now() - xact_start > interval '{LongTransactionAfter.TotalSeconds:0} seconds'
              AND pid <> pg_backend_pid()
            ORDER BY xact_start
            LIMIT 50
            """, reader => new
            {
                Pid = reader.GetInt32(0),
                State = reader.GetString(1),
                Age = reader.GetFieldValue<TimeSpan>(2),
            }, ct);
        issues.Add(new DatabaseHealthIssue(
            DatabaseHealthIssueKeys.LongRunningTransactions,
            "Transações longas (bloqueiam VACUUM)",
            "warning",
            longTransactions.Count,
            longTransactions.Take(MaxSamples).Select(t => $"pid {t.Pid} ({t.State}) há {t.Age.TotalMinutes:0.#} min").ToList()));

        return new DatabaseHealthReport(
            generatedAt,
            databaseSize,
            tables.Sum(t => t.TotalBytes),
            tables,
            issues);
    }

    // ── Helpers ADO.NET ─────────────────────────────────────────────────────

    private async Task<long?> ScalarLongAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            var value = await cmd.ExecuteScalarAsync(ct);
            return value is null or DBNull ? null : Convert.ToInt64(value);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Diagnóstico do banco: consulta escalar falhou ({Sql}).", Trim(sql));
            return null;
        }
    }

    private async Task<List<T>> QueryAsync<T>(
        DbConnection connection, string sql, Func<DbDataReader, T> map, CancellationToken ct)
    {
        var results = new List<T>();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(map(reader));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Diagnóstico do banco: consulta falhou ({Sql}).", Trim(sql));
        }
        return results;
    }

    private static DateTime? ReadUtc(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        var value = reader.GetDateTime(ordinal);
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    private static double DeadPercent(long live, long dead)
    {
        var total = live + dead;
        return total <= 0 ? 0d : Math.Round(100d * dead / total, 2);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.##} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.##} MB",
        >= 1_024 => $"{bytes / 1_024d:0.##} KB",
        _ => $"{bytes} B",
    };

    private static string Trim(string sql)
    {
        var flat = sql.Replace('\n', ' ').Replace('\r', ' ');
        var sb = new StringBuilder();
        var lastWasSpace = false;
        foreach (var c in flat)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(c);
            lastWasSpace = false;
        }
        var text = sb.ToString().Trim();
        return text.Length <= 120 ? text : text[..120] + "...";
    }
}

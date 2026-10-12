using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Manutenção física do PostgreSQL, guiada por <see cref="DatabaseMaintenanceSettings"/>.
///
/// <para>Fases (todas tolerantes a falha por tabela/índice):
/// VACUUM ANALYZE (rotina leve) → VACUUM FULL (só bloat ou escopo "all") →
/// VACUUM FREEZE (wraparound) → REINDEX (índices inválidos sempre; "all" se
/// configurado, concorrente por default) → ANALYZE (estatística velha).</para>
///
/// <para>O job sempre aplica <c>lock_timeout</c> (falha em vez de travar horas) e
/// <c>maintenance_work_mem</c> na sessão, mede o tamanho do banco antes/depois e
/// persiste o resumo em <c>database_maintenance_runs</c>.</para>
/// </summary>
[DisallowConcurrentExecution]
public sealed class DatabaseMaintenanceJob : IJob
{
    public static readonly JobKey Key = new("database-maintenance", "maintenance");

    /// <summary>Tabelas de metadados que nunca precisam de manutenção.</summary>
    private static readonly HashSet<string> ExcludedTables =
        new(StringComparer.OrdinalIgnoreCase) { "VersionInfo" };

    private static readonly Regex SafeIdentifier = new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<DatabaseMaintenanceJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var serverRepo = scope.ServiceProvider.GetRequiredService<IServerConfigurationRepository>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var server = await serverRepo.GetOrCreateDefaultAsync();

        var settings = DatabaseMaintenanceSettings.Parse(server.RetentionSettingsJson);
        if (!settings.Enabled)
        {
            logger.LogInformation("Database maintenance is disabled in RetentionSettings.");
            return;
        }

        var startedAt = DateTime.UtcNow;
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        var sizeBefore = await ScalarLongAsync(connection, "SELECT pg_database_size(current_database())", ct) ?? 0;

        await ApplySessionSettingsAsync(connection, settings, ct);

        var tables = await DiscoverTablesAsync(connection, ct);
        var invalidIndexes = await DiscoverInvalidIndexesAsync(connection, ct);
        var invalidIndexNames = invalidIndexes.Select(i => i.Index).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targets = settings.MaxTablesPerRun > 0
            ? tables.OrderByDescending(t => t.TotalBytes).Take(settings.MaxTablesPerRun).ToList()
            : tables.OrderByDescending(t => t.TotalBytes).ToList();

        logger.LogInformation(
            "Database maintenance: {Tables} tabelas descobertas, {Invalid} índices inválidos. VacuumAnalyze={VA}, VacuumFull={VF}/{Scope}, Reindex={RI}/{RScope}, Analyze={AN}, Freeze={FZ}",
            tables.Count, invalidIndexes.Count, settings.VacuumAnalyze, settings.VacuumFull, settings.VacuumFullScope,
            settings.Reindex, settings.ReindexScope, settings.Analyze, settings.VacuumFreeze);

        var operations = new List<MaintenanceOperation>();
        var fullyVacuumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Fase 1: VACUUM ANALYZE (rotina leve, não bloqueia escrita) ──────
        if (settings.VacuumAnalyze)
        {
            foreach (var table in targets)
            {
                if (ct.IsCancellationRequested) break;
                await RunOperationAsync(operations, connection, "vacuum-analyze", table.Name,
                    $"VACUUM (ANALYZE) {Quote(table.Name)}", logger, ct);
            }
        }

        // ── Fase 2: VACUUM FULL (reescrita; só bloat ou escopo "all") ───────
        if (settings.VacuumFull && settings.VacuumFullScope != DatabaseMaintenanceScopes.None)
        {
            var fullTargets = settings.VacuumFullScope == DatabaseMaintenanceScopes.All
                ? targets
                : targets.Where(t => t.DeadPercent >= settings.VacuumFullBloatPercent && t.DeadTuples >= 1_000).ToList();

            foreach (var table in fullTargets)
            {
                if (ct.IsCancellationRequested) break;
                if (await RunOperationAsync(operations, connection, "vacuum-full", table.Name,
                        $"VACUUM (FULL, ANALYZE) {Quote(table.Name)}", logger, ct))
                    fullyVacuumed.Add(table.Name);
            }
        }

        // ── Fase 3: VACUUM FREEZE (proteção contra wraparound de XID) ───────
        if (settings.VacuumFreeze)
        {
            foreach (var table in targets)
            {
                if (ct.IsCancellationRequested) break;
                if (table.XidAge < settings.FreezeMinXidAge) continue;
                if (fullyVacuumed.Contains(table.Name)) continue; // FULL já congela
                await RunOperationAsync(operations, connection, "vacuum-freeze", table.Name,
                    $"VACUUM (FREEZE, ANALYZE) {Quote(table.Name)}", logger, ct);
            }
        }

        // ── Fase 4: REINDEX (inválidos SEMPRE; "all" sob configuração) ──────
        if (settings.ReindexScope != DatabaseReindexScopes.None)
        {
            foreach (var index in invalidIndexes)
            {
                if (ct.IsCancellationRequested) break;
                await RunReindexAsync(operations, connection, "reindex-invalid", index.Index,
                    concurrentlySql: $"REINDEX INDEX CONCURRENTLY {Quote(index.Index)}",
                    plainSql: $"REINDEX INDEX {Quote(index.Index)}",
                    useConcurrently: settings.ReindexConcurrently, logger, ct);
            }
        }

        if (settings.Reindex && settings.ReindexScope == DatabaseReindexScopes.All)
        {
            foreach (var table in targets)
            {
                if (ct.IsCancellationRequested) break;
                await RunReindexAsync(operations, connection, "reindex-table", table.Name,
                    concurrentlySql: $"REINDEX TABLE CONCURRENTLY {Quote(table.Name)}",
                    plainSql: $"REINDEX TABLE {Quote(table.Name)}",
                    useConcurrently: settings.ReindexConcurrently, logger, ct);
            }
        }
        else if (invalidIndexNames.Count > 0 && settings.ReindexScope == DatabaseReindexScopes.None)
        {
            logger.LogWarning(
                "REINDEX desligado, mas {Count} índices inválidos foram reparados (integridade).",
                invalidIndexNames.Count);
        }

        // ── Fase 5: ANALYZE (só estatística velha) ──────────────────────────
        if (settings.Analyze)
        {
            var staleBefore = DateTime.UtcNow.AddDays(-settings.AnalyzeStaleAfterDays);
            foreach (var table in targets)
            {
                if (ct.IsCancellationRequested) break;
                if (table.LastAnalyzeUtc is { } last && last >= staleBefore) continue;
                await RunOperationAsync(operations, connection, "analyze", table.Name,
                    $"ANALYZE {Quote(table.Name)}", logger, ct);
            }
        }

        var sizeAfter = await ScalarLongAsync(connection, "SELECT pg_database_size(current_database())", ct) ?? sizeBefore;
        var successCount = operations.Count(o => o.Ok);
        var errorCount = operations.Count(o => !o.Ok);
        var finishedAt = DateTime.UtcNow;

        logger.LogInformation(
            "Database maintenance completed in {Duration:0.#}s. {Success} operações ok, {Errors} falhas, {Tables} tabelas. Tamanho {Before} → {After} bytes.",
            (finishedAt - startedAt).TotalSeconds, successCount, errorCount, tables.Count, sizeBefore, sizeAfter);

        await SaveRunAsync(db, startedAt, finishedAt, successCount, errorCount, tables.Count,
            sizeAfter, sizeBefore - sizeAfter, operations, logger, ct);

        context.Result = new
        {
            startedAtUtc = startedAt,
            finishedAtUtc = finishedAt,
            durationSeconds = Math.Round((finishedAt - startedAt).TotalSeconds, 2),
            tableCount = tables.Count,
            invalidIndexCount = invalidIndexes.Count,
            successCount,
            errorCount,
            databaseSizeBeforeBytes = sizeBefore,
            databaseSizeAfterBytes = sizeAfter,
            reclaimedBytes = sizeBefore - sizeAfter,
            operations = operations.Select(o => new { o.Phase, o.Target, o.Ok, o.DurationMs, o.Error }),
        };
    }

    // ── Descoberta ──────────────────────────────────────────────────────────

    private sealed record TableInfo(
        string Name, long TotalBytes, long LiveTuples, long DeadTuples,
        double DeadPercent, DateTime? LastAnalyzeUtc, long XidAge);

    private sealed record IndexInfo(string Table, string Index);

    private sealed record MaintenanceOperation(
        string Phase, string Target, bool Ok, long DurationMs, string? Error);

    private static async Task<List<TableInfo>> DiscoverTablesAsync(DbConnection connection, CancellationToken ct)
    {
        var tables = new List<TableInfo>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.relname,
                   pg_total_relation_size(c.oid)               AS total_bytes,
                   COALESCE(s.n_live_tup, 0)                   AS live_tuples,
                   COALESCE(s.n_dead_tup, 0)                   AS dead_tuples,
                   s.last_analyze,
                   age(c.relfrozenxid)                         AS xid_age
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
            ORDER BY total_bytes DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (ExcludedTables.Contains(name) || !SafeIdentifier.IsMatch(name))
                continue;

            var live = Convert.ToInt64(reader.GetValue(2));
            var dead = Convert.ToInt64(reader.GetValue(3));
            var total = live + dead;
            tables.Add(new TableInfo(
                name,
                reader.GetInt64(1),
                live,
                dead,
                total <= 0 ? 0d : Math.Round(100d * dead / total, 2),
                ReadUtc(reader, 4),
                Convert.ToInt64(reader.GetValue(5))));
        }
        return tables;
    }

    private static async Task<List<IndexInfo>> DiscoverInvalidIndexesAsync(DbConnection connection, CancellationToken ct)
    {
        var indexes = new List<IndexInfo>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT tc.relname AS table_name, ic.relname AS index_name
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_class tc ON tc.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = ic.relnamespace
            WHERE NOT i.indisvalid AND n.nspname = 'public'
            ORDER BY tc.relname, ic.relname
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            var index = reader.GetString(1);
            if (SafeIdentifier.IsMatch(table) && SafeIdentifier.IsMatch(index))
                indexes.Add(new IndexInfo(table, index));
        }
        return indexes;
    }

    // ── Execução ────────────────────────────────────────────────────────────

    private static async Task ApplySessionSettingsAsync(
        DbConnection connection, DatabaseMaintenanceSettings settings, CancellationToken ct)
    {
        if (settings.LockTimeoutSeconds > 0)
            await ExecuteRawAsync(connection, $"SET lock_timeout = '{settings.LockTimeoutSeconds}s'", ct);
        await ExecuteRawAsync(connection, $"SET maintenance_work_mem = '{settings.MaintenanceWorkMemMb}MB'", ct);
    }

    private static async Task<bool> RunOperationAsync(
        List<MaintenanceOperation> operations, DbConnection connection,
        string phase, string target, string sql, ILogger logger, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await ExecuteRawAsync(connection, sql, ct);
            sw.Stop();
            operations.Add(new MaintenanceOperation(phase, target, true, sw.ElapsedMilliseconds, null));
            logger.LogDebug("Database maintenance: {Phase} {Target} ok em {Ms}ms.", phase, target, sw.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            operations.Add(new MaintenanceOperation(phase, target, false, sw.ElapsedMilliseconds, ex.Message));
            logger.LogError(ex, "Database maintenance: {Phase} {Target} falhou.", phase, target);
            return false;
        }
    }

    /// <summary>
    /// REINDEX com fallback: <c>CONCURRENTLY</c> não é suportado em alguns casos
    /// (ex.: índice de tabela particionada). Se falhar, tenta a forma bloqueante
    /// para não deixar o índice inválido sem reparo.
    /// </summary>
    private static async Task RunReindexAsync(
        List<MaintenanceOperation> operations, DbConnection connection,
        string phase, string target, string concurrentlySql, string plainSql,
        bool useConcurrently, ILogger logger, CancellationToken ct)
    {
        if (!useConcurrently)
        {
            await RunOperationAsync(operations, connection, phase, target, plainSql, logger, ct);
            return;
        }

        if (!await RunOperationAsync(operations, connection, phase, target, concurrentlySql, logger, ct))
            await RunOperationAsync(operations, connection, phase + "-fallback", target, plainSql, logger, ct);
    }

    private static async Task ExecuteRawAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long?> ScalarLongAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            var value = await cmd.ExecuteScalarAsync(ct);
            return value is null or DBNull ? null : Convert.ToInt64(value);
        }
        catch
        {
            return null;
        }
    }

    private static async Task SaveRunAsync(
        DiscoveryDbContext db, DateTime startedAt, DateTime finishedAt,
        int successCount, int errorCount, int tableCount,
        long databaseSizeBytes, long reclaimedBytes,
        List<MaintenanceOperation> operations, ILogger logger, CancellationToken ct)
    {
        try
        {
            var errors = operations.Where(o => !o.Ok).Select(o => $"{o.Phase}:{o.Target}: {o.Error}").ToList();
            var run = new DatabaseMaintenanceRun
            {
                Id = Guid.NewGuid(),
                StartedAtUtc = startedAt,
                FinishedAtUtc = finishedAt,
                Succeeded = errorCount == 0,
                TableCount = tableCount,
                SuccessCount = successCount,
                ErrorCount = errorCount,
                DatabaseSizeBytes = databaseSizeBytes,
                ReclaimedBytes = reclaimedBytes,
                OperationsJson = JsonSerializer.Serialize(operations),
                ErrorMessage = errors.Count == 0 ? null : string.Join(" | ", errors.Take(5)),
            };
            db.DatabaseMaintenanceRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // O histórico não pode derrubar a manutenção já executada.
            logger.LogWarning(ex, "Database maintenance: falha ao persistir o histórico da execução.");
        }
    }

    private static string Quote(string identifier) =>
        QuoteIdentifier(identifier);

    private static string QuoteIdentifier(string identifier)
    {
        if (!SafeIdentifier.IsMatch(identifier))
            throw new InvalidOperationException($"Identificador inválido para manutenção: {identifier}");
        return "\"" + identifier + "\"";
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
}

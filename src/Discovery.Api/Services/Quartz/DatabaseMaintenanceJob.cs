using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Database maintenance job that performs VACUUM, REINDEX, and ANALYZE
/// on ALL user tables to reclaim disk space and optimize query performance.
///
/// Operations (controlled by boolean flags in RetentionSettings):
/// - VACUUM FULL: reclaims disk space from dead tuples (heavy, locks tables)
/// - REINDEX: rebuilds fragmented indexes
/// - ANALYZE: updates query planner statistics
/// - VACUUM ANALYZE: lighter vacuum with statistics (non-blocking)
///
/// Schedule: weekly Sunday 3 AM (configurable via RetentionSettings)
/// </summary>
[DisallowConcurrentExecution]
public sealed class DatabaseMaintenanceJob : IJob
{
    public static readonly JobKey Key = new("database-maintenance", "maintenance");

    /// <summary>
    /// Table names/patterns to exclude from maintenance operations.
    /// VersionInfo is FluentMigrator metadata, never needs maintenance.
    /// </summary>
    private static readonly string[] ExcludedTablePatterns = ["VersionInfo", "pg_%", "information_schema.%"];

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

        var settings = ParseMaintenanceSettings(server.RetentionSettingsJson);
        if (settings is not { Enabled: true })
        {
            logger.LogInformation("Database maintenance is disabled in RetentionSettings.");
            return;
        }

        var tables = await DiscoverUserTablesAsync(db, ct);
        logger.LogInformation("Database maintenance: discovered {Count} user tables.", tables.Count);

        var results = new Dictionary<string, int>();

        // ── VACUUM FULL ──
        if (settings.VacuumFull)
        {
            foreach (var table in tables)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await db.Database.ExecuteSqlRawAsync($"VACUUM FULL \"{table}\"", ct);
                    results[$"vacuum_{table}"] = 1;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "VACUUM FULL \"{Table}\" failed.", table);
                    results[$"vacuum_{table}_error"] = 1;
                }
            }
        }

        // ── REINDEX ──
        if (settings.Reindex)
        {
            foreach (var table in tables)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await db.Database.ExecuteSqlRawAsync($"REINDEX TABLE \"{table}\"", ct);
                    results[$"reindex_{table}"] = 1;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "REINDEX \"{Table}\" failed.", table);
                    results[$"reindex_{table}_error"] = 1;
                }
            }
        }

        // ── ANALYZE ──
        if (settings.Analyze)
        {
            foreach (var table in tables)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await db.Database.ExecuteSqlRawAsync($"ANALYZE \"{table}\"", ct);
                    results[$"analyze_{table}"] = 1;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ANALYZE \"{Table}\" failed.", table);
                    results[$"analyze_{table}_error"] = 1;
                }
            }
        }

        // ── VACUUM ANALYZE ──
        if (settings.VacuumAnalyze)
        {
            foreach (var table in tables)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await db.Database.ExecuteSqlRawAsync($"VACUUM ANALYZE \"{table}\"", ct);
                    results[$"vacuum_analyze_{table}"] = 1;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "VACUUM ANALYZE \"{Table}\" failed.", table);
                    results[$"vacuum_analyze_{table}_error"] = 1;
                }
            }
        }

        var successCount = results.Count(r => !r.Key.EndsWith("_error"));
        var errorCount = results.Count(r => r.Key.EndsWith("_error"));

        logger.LogInformation(
            "Database maintenance completed. {Success} operations succeeded, {Errors} failed on {TableCount} tables. VacuumFull={VF}, Reindex={RI}, Analyze={AN}, VacuumAnalyze={VA}",
            successCount, errorCount, tables.Count,
            settings.VacuumFull, settings.Reindex, settings.Analyze, settings.VacuumAnalyze);

        context.Result = new { successCount, errorCount, tableCount = tables.Count, operations = results };
    }

    /// <summary>
    /// Discovers all user tables (excluding system catalog and VersionInfo).
    /// </summary>
    private static async Task<List<string>> DiscoverUserTablesAsync(DiscoveryDbContext db, CancellationToken ct)
    {
        var allTables = new List<string>();

        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = @"
            SELECT tablename
            FROM pg_catalog.pg_tables
            WHERE schemaname = 'public'
              AND tableowner = current_user
            ORDER BY tablename";

        if (cmd.Connection?.State != System.Data.ConnectionState.Open)
            await cmd.Connection!.OpenAsync(ct);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var excluded = ExcludedTablePatterns.Any(pattern =>
                pattern.Contains('%') || pattern.Contains('.')
                    ? false // não usamos LIKE, excluímos por nome exato ou prefixo
                    : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase));

            if (!excluded && SafeIdentifier.IsMatch(name))
                allTables.Add(name);
        }

        allTables.RemoveAll(t => string.Equals(t, "VersionInfo", StringComparison.OrdinalIgnoreCase));
        return allTables;
    }

    private static DatabaseMaintenanceSettings? ParseMaintenanceSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return null;

        try
        {
            var settings = JsonSerializer.Deserialize<RetentionSettings>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return settings?.DatabaseMaintenance;
        }
        catch
        {
            return null;
        }
    }
}

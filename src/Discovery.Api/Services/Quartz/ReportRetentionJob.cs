using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;
using Quartz;
using System.Text.Json;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job that purges old report executions and their generated files.
/// Replaces ReportRetentionBackgroundService.
/// Schedule: daily at 4 AM (0 0 4 * * ?)
/// </summary>
[DisallowConcurrentExecution]
public sealed class ReportRetentionJob : IJob
{
    public static readonly JobKey Key = new("report-retention", "maintenance");
    private const int DefaultRetentionDays = 90;
    private static readonly int[] DefaultAllowedDays = [30, 60, 90];

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var optionsMonitor = context.GetScopedService<IOptionsMonitor<ReportingOptions>>();
        var logger = context.GetLogger<ReportRetentionJob>();
        var ct = context.CancellationToken;

        var fallback = optionsMonitor.CurrentValue;
        using var scope = scopeFactory.CreateScope();

        var serverRepo = scope.ServiceProvider.GetRequiredService<IServerConfigurationRepository>();
        var server = await serverRepo.GetOrCreateDefaultAsync();
        var options = ResolveEffectiveOptions(server.ReportingSettingsJson, fallback);
        var dbRetentionDays = ValidateRetentionDays(options.DatabaseRetentionDays, options.AllowedRetentionDays);
        var fileRetentionDays = ValidateRetentionDays(options.FileRetentionDays, options.AllowedRetentionDays);

        var dbCutoff = DateTime.UtcNow.AddDays(-dbRetentionDays);
        var fileCutoff = DateTime.UtcNow.AddDays(-fileRetentionDays);

        var reportRepo = scope.ServiceProvider.GetRequiredService<IReportExecutionRepository>();
        var storageFactory = scope.ServiceProvider.GetRequiredService<IObjectStorageProviderFactory>();
        var storageValidationErrors = await storageFactory.ValidateConfigurationAsync();
        if (storageValidationErrors.Count > 0)
        {
            logger.LogWarning("Report retention skipped: object storage misconfigured. {Errors}", storageValidationErrors);
            return;
        }

        var dbDeleted = await reportRepo.PurgeExpiredExecutionsAsync(dbCutoff, ct);
        logger.LogInformation("Report retention: purged {Count} DB records older than {Days}d.", dbDeleted, dbRetentionDays);

        var filesDeleted = 0;
        var filesFailed = 0;
        var oldFiles = await reportRepo.GetExpiredFilesAsync(fileCutoff, 1000, ct);
        foreach (var file in oldFiles)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(file.StorageObjectKey))
                {
                    var storage = storageFactory.CreateObjectStorageService();
                    await storage.DeleteFileAsync(file.StorageObjectKey, file.StorageBucket, ct);
                    await reportRepo.MarkFileDeletedAsync(file.Id, ct);
                    filesDeleted++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete report file {Key}", file.StorageObjectKey);
                filesFailed++;
            }
        }

        logger.LogInformation("Report retention: deleted {FileCount} files, {Failed} failures.", filesDeleted, filesFailed);
        context.Result = new { dbDeleted, filesDeleted, filesFailed };
    }

    private static ReportingOptions ResolveEffectiveOptions(string? settingsJson, ReportingOptions fallback)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return fallback;

        try
        {
            var server = JsonSerializer.Deserialize<ReportingOptions>(settingsJson);
            return server ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static int ValidateRetentionDays(int value, int[]? allowedValues)
    {
        var allowed = allowedValues is { Length: > 0 } ? allowedValues : DefaultAllowedDays;
        return allowed.Contains(value) ? value : DefaultRetentionDays;
    }
}

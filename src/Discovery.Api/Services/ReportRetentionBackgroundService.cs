using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Discovery.Api.Services;

public class ReportRetentionBackgroundService : BackgroundService
{
    private const int DefaultRetentionDays = 90;
    private static readonly int[] DefaultAllowedDays = [30, 60, 90];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<ReportingOptions> _optionsMonitor;
    private readonly ILogger<ReportRetentionBackgroundService> _logger;

    public ReportRetentionBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<ReportingOptions> optionsMonitor,
        ILogger<ReportRetentionBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PurgeOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PurgeOnceAsync(stoppingToken);
        }
    }

    private async Task PurgeOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            var fallback = _optionsMonitor.CurrentValue;

            using var scope = _scopeFactory.CreateScope();
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
                _logger.LogWarning(
                    "Report retention skipped: object storage not configured. Errors: {Errors}",
                    string.Join("; ", storageValidationErrors));
                return;
            }

            var storageService = storageFactory.CreateObjectStorageService();

            var expired = await reportRepo.GetExpiredAsync(dbCutoff, 5000);
            if (expired.Count == 0)
            {
                _logger.LogInformation(
                    "Report retention checked. Nothing to purge. DB days: {DbDays}, File days: {FileDays}",
                    dbRetentionDays,
                    fileRetentionDays);
                return;
            }

            var deletedFiles = 0;
            var fileErrors = 0;

            foreach (var execution in expired)
            {
                if (string.IsNullOrWhiteSpace(execution.StorageObjectKey))
                    continue;

                if (execution.CreatedAt > fileCutoff)
                    continue;

                try
                {
                    await storageService.DeleteAsync(execution.StorageObjectKey, stoppingToken);
                    deletedFiles++;
                }
                catch (Exception ex)
                {
                    fileErrors++;
                    _logger.LogWarning(ex, "Failed to delete report object: {ObjectKey}", execution.StorageObjectKey);
                }
            }

            var deletedRows = await reportRepo.DeleteByIdsAsync(expired.Select(execution => execution.Id).ToArray());

            _logger.LogInformation(
                "Report retention purge completed. Deleted rows: {DeletedRows}, deleted files: {DeletedFiles}, file errors: {FileErrors}, DB days: {DbDays}, File days: {FileDays}",
                deletedRows,
                deletedFiles,
                fileErrors,
                dbRetentionDays,
                fileRetentionDays);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Report retention purge failed.");
        }
    }

    private static int ValidateRetentionDays(int configuredDays, int[]? allowedDays)
    {
        var allowed = (allowedDays is { Length: > 0 } ? allowedDays : DefaultAllowedDays)
            .Distinct()
            .OrderBy(day => day)
            .ToArray();

        if (!allowed.Contains(configuredDays))
            return DefaultRetentionDays;

        return configuredDays;
    }

    private static ReportingOptions ResolveEffectiveOptions(string? persistedJson, ReportingOptions fallback)
    {
        if (string.IsNullOrWhiteSpace(persistedJson))
            return fallback;

        try
        {
            var persisted = JsonSerializer.Deserialize<ReportingOptions>(persistedJson, JsonSerializerOptions.Web);
            if (persisted is null)
                return fallback;

            return new ReportingOptions
            {
                EnablePdf = persisted.EnablePdf,
                ProcessingTimeoutSeconds = persisted.ProcessingTimeoutSeconds > 0 ? persisted.ProcessingTimeoutSeconds : fallback.ProcessingTimeoutSeconds,
                FileDownloadTimeoutSeconds = persisted.FileDownloadTimeoutSeconds > 0 ? persisted.FileDownloadTimeoutSeconds : fallback.FileDownloadTimeoutSeconds,
                DatabaseRetentionDays = persisted.DatabaseRetentionDays > 0 ? persisted.DatabaseRetentionDays : fallback.DatabaseRetentionDays,
                FileRetentionDays = persisted.FileRetentionDays > 0 ? persisted.FileRetentionDays : fallback.FileRetentionDays,
                AllowedRetentionDays = persisted.AllowedRetentionDays is { Length: > 0 } ? persisted.AllowedRetentionDays : fallback.AllowedRetentionDays
            };
        }
        catch
        {
            return fallback;
        }
    }
}

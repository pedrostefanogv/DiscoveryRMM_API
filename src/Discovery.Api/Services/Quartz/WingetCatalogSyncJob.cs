using Discovery.Core.Interfaces;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que sincroniza o catálogo de aplicativos Winget periodicamente.
/// Configurável via appsettings: BackgroundJobs:WingetCatalogSync:IntervalDays (default: 5)
/// e BackgroundJobs:WingetCatalogSync:Enabled (default: false).
///
/// Schedule: a cada N dias à meia-noite (ajustável via config).
/// </summary>
[DisallowConcurrentExecution]
public sealed class WingetCatalogSyncJob : IJob
{
    public static readonly JobKey Key = new("winget-catalog-sync", "catalog");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var config = context.GetScopedService<IConfiguration>();
        var logger = context.GetLogger<WingetCatalogSyncJob>();
        var ct = context.CancellationToken;

        var enabled = config.GetValue<bool?>("BackgroundJobs:WingetCatalogSync:Enabled") ?? false;
        if (!enabled)
        {
            logger.LogDebug("WingetCatalogSync is disabled (BackgroundJobs:WingetCatalogSync:Enabled = false).");
            return;
        }

        logger.LogInformation("Winget catalog sync starting...");

        await using var scope = scopeFactory.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IWingetPackageSyncService>();

        try
        {
            var result = await syncService.SyncCatalogAsync(ct);
            context.Result = result;

            if (result.Success)
            {
                logger.LogInformation(
                    "Winget catalog sync successful: {Count} packages in {Duration}.",
                    result.PackagesUpserted, result.Duration);
            }
            else
            {
                logger.LogWarning(
                    "Winget catalog sync completed with issues: {Error}",
                    result.Error ?? "unknown");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Winget catalog sync failed.");
            throw new JobExecutionException(ex) { RefireImmediately = false };
        }
    }
}

using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que sincroniza o catálogo Winget a partir do shallow clone do
/// microsoft/winget-pkgs (fonte primária, rev. 3 do plano).
/// Config: AppCatalog:Winget:Enabled / ManifestsPollIntervalMinutes / Source.
/// </summary>
[DisallowConcurrentExecution]
public sealed class WingetManifestsSyncJob : IJob
{
    public static readonly JobKey Key = new("winget-manifests-sync", "catalog");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var config = context.GetScopedService<IConfiguration>();
        var logger = context.GetLogger<WingetManifestsSyncJob>();
        var ct = context.CancellationToken;

        var options = config.GetSection(WingetManifestsSyncOptions.SectionName).Get<WingetManifestsSyncOptions>()
                      ?? new WingetManifestsSyncOptions();

        if (!options.Enabled || options.Source.Equals("feed", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("WingetManifestsSync desabilitado (Enabled={Enabled}, Source={Source}).", options.Enabled, options.Source);
            return;
        }

        logger.LogInformation("Winget manifests sync starting...");

        await using var scope = scopeFactory.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IWingetManifestsSyncService>();

        try
        {
            var result = await syncService.SyncFromManifestsAsync(ct);
            context.Result = result;

            if (result.Success)
                logger.LogInformation(
                    "Winget manifests sync successful: {Count} packages in {Duration}.",
                    result.PackagesUpserted, result.Duration);
            else
                logger.LogWarning("Winget manifests sync completed with issues: {Error}", result.Error ?? "unknown");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Winget manifests sync failed.");
            throw new JobExecutionException(ex) { RefireImmediately = false };
        }
    }
}

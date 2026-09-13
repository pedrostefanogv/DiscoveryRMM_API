using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que sincroniza o catálogo Winget a partir do shallow clone do
/// microsoft/winget-pkgs (fonte primária, rev. 3 do plano).
/// Config: AppCatalog:Winget:Enabled / ManifestsPollIntervalMinutes / Source.
/// O resultado de cada execução (sucesso ou falha) é persistido no
/// IAppCatalogSyncStatusStore para aparecer no status do console web.
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
        var statusStore = scope.ServiceProvider.GetRequiredService<IAppCatalogSyncStatusStore>();

        try
        {
            var result = await syncService.SyncFromManifestsAsync(ct);

            // Persiste o resultado para o console web: execuções automáticas
            // antes não atualizavam o status, que era alimentado apenas pelo
            // sync manual (AppCatalogBackgroundSyncService, em memória).
            await statusStore.SaveResultAsync(AppInstallationType.Winget, result, ct);

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

            // Persiste a falha também: o status no console precisa refletir o
            // problema mesmo quando o job termina com exceção.
            try
            {
                await statusStore.SaveResultAsync(AppInstallationType.Winget, new AppCatalogSyncResultDto
                {
                    InstallationType = AppInstallationType.Winget,
                    Success = false,
                    PackagesUpserted = 0,
                    PagesProcessed = 0,
                    SyncedAt = DateTime.UtcNow,
                    Duration = TimeSpan.Zero,
                    Error = ex.Message
                }, CancellationToken.None);
            }
            catch (Exception persistEx)
            {
                logger.LogWarning(persistEx, "Falha ao persistir status de falha do sync Winget.");
            }

            throw new JobExecutionException(ex) { RefireImmediately = false };
        }
    }
}

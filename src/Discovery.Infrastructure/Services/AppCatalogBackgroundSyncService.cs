using System.Collections.Concurrent;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Orquestra sincronizações de catálogo (Winget/Chocolatey) em background.
/// O POST /sync dispara o job e retorna imediatamente (evita timeout de gateway em syncs longos);
/// o status/resultado é consultável via GET /sync/status.
/// Garante no máximo 1 sync simultâneo por InstallationType.
/// </summary>
public class AppCatalogBackgroundSyncService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AppCatalogBackgroundSyncService> _logger;

    private readonly ConcurrentDictionary<AppInstallationType, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<AppInstallationType, AppCatalogSyncResultDto> _lastResults = new();

    public AppCatalogBackgroundSyncService(
        IServiceProvider serviceProvider,
        IHostApplicationLifetime applicationLifetime,
        ILogger<AppCatalogBackgroundSyncService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        // Propaga o shutdown da aplicação para os jobs em andamento.
        applicationLifetime.ApplicationStopping.Register(() =>
        {
            foreach (var (type, cts) in _running)
            {
                _logger.LogInformation("Cancelando sync de catálogo em andamento ({Type}) devido ao shutdown.", type);
                cts.Cancel();
            }
        });
    }

    /// <summary>Resultado da última sync concluída para o tipo (null se nunca executada nesta instância).</summary>
    public AppCatalogSyncResultDto? GetLastResult(AppInstallationType installationType) =>
        _lastResults.TryGetValue(installationType, out var result) ? result : null;

    /// <summary>Indica se há sync em andamento para o tipo.</summary>
    public bool IsRunning(AppInstallationType installationType) => _running.ContainsKey(installationType);

    /// <summary>
    /// Tenta iniciar uma sync em background. Retorna false se já houver uma em andamento para o tipo.
    /// </summary>
    public bool TryStartSync(AppInstallationType installationType)
    {
        if (installationType == AppInstallationType.Custom)
            return false;

        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(installationType, cts))
        {
            cts.Dispose();
            return false; // já existe sync em andamento
        }

        _logger.LogInformation("Background catalog sync started for {Type}.", installationType);
        _ = Task.Run(() => RunSyncAsync(installationType, cts));
        return true;
    }

    private async Task RunSyncAsync(AppInstallationType installationType, CancellationTokenSource cts)
    {
        try
        {
            // Cria scope próprio: o sync service e repositórios são scoped.
            using var scope = _serviceProvider.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<IAppCatalogSyncService>();
            var result = await syncService.SyncCatalogAsync(installationType, cts.Token);
            _lastResults[installationType] = result;

            if (result.Success)
                _logger.LogInformation("Background catalog sync completed for {Type}: {Upserted} packages, {Pages} pages, {Duration}.",
                    installationType, result.PackagesUpserted, result.PagesProcessed, result.Duration);
            else
                _logger.LogWarning("Background catalog sync failed for {Type}: {Error}", installationType, result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background catalog sync crashed for {Type}.", installationType);
            _lastResults[installationType] = new AppCatalogSyncResultDto
            {
                InstallationType = installationType,
                Success = false,
                PackagesUpserted = 0,
                PagesProcessed = 0,
                SyncedAt = DateTime.UtcNow,
                Duration = TimeSpan.Zero,
                Error = ex.Message
            };
        }
        finally
        {
            _running.TryRemove(installationType, out var removed);
            removed?.Dispose();
        }
    }
}

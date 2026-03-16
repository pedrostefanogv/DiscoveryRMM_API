using Meduza.Core.Configuration;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Meduza.Api.Services;

public class MeshCentralIdentityReconciliationBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly MeshCentralOptions _options;
    private readonly ILogger<MeshCentralIdentityReconciliationBackgroundService> _logger;

    public MeshCentralIdentityReconciliationBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<MeshCentralOptions> options,
        ILogger<MeshCentralIdentityReconciliationBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.IdentitySyncEnabled || !_options.IdentitySyncReconciliationEnabled)
        {
            _logger.LogInformation("MeshCentral identity reconciliation background service is disabled by configuration.");
            return;
        }

        var startupDelaySeconds = Math.Max(0, _options.IdentitySyncReconciliationStartupDelaySeconds);
        if (startupDelaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(startupDelaySeconds), stoppingToken);
        }

        var intervalMinutes = Math.Max(1, _options.IdentitySyncReconciliationIntervalMinutes);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        _logger.LogInformation(
            "MeshCentral identity reconciliation background service started. IntervalMinutes={IntervalMinutes}, ApplyChanges={ApplyChanges}.",
            intervalMinutes,
            _options.IdentitySyncReconciliationApplyChanges);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = _serviceProvider.CreateAsyncScope();
                var syncService = scope.ServiceProvider.GetRequiredService<IMeshCentralIdentitySyncService>();

                var report = await syncService.RunBackfillAsync(
                    applyChanges: _options.IdentitySyncReconciliationApplyChanges,
                    cancellationToken: stoppingToken);

                _logger.LogInformation(
                    "MeshCentral identity reconciliation finished. ApplyChanges={ApplyChanges}, TotalUsers={TotalUsers}, SyncedUsers={SyncedUsers}, FailedUsers={FailedUsers}, DurationMs={DurationMs}.",
                    report.ApplyChanges,
                    report.TotalUsers,
                    report.SyncedUsers,
                    report.FailedUsers,
                    (report.FinishedAtUtc - report.StartedAtUtc).TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MeshCentral identity reconciliation cycle failed.");
            }
        }
    }
}

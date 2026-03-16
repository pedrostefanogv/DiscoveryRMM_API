using Meduza.Core.Configuration;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Meduza.Api.Services;

public class MeshCentralGroupPolicyReconciliationBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly MeshCentralOptions _options;
    private readonly ILogger<MeshCentralGroupPolicyReconciliationBackgroundService> _logger;

    public MeshCentralGroupPolicyReconciliationBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<MeshCentralOptions> options,
        ILogger<MeshCentralGroupPolicyReconciliationBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.GroupPolicyReconciliationEnabled)
        {
            _logger.LogInformation("MeshCentral group policy reconciliation background service is disabled by configuration.");
            return;
        }

        var startupDelaySeconds = Math.Max(0, _options.GroupPolicyReconciliationStartupDelaySeconds);
        if (startupDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(startupDelaySeconds), stoppingToken);

        var intervalMinutes = Math.Max(1, _options.GroupPolicyReconciliationIntervalMinutes);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        _logger.LogInformation(
            "MeshCentral group policy reconciliation background service started. IntervalMinutes={IntervalMinutes}, ApplyChanges={ApplyChanges}.",
            intervalMinutes,
            _options.GroupPolicyReconciliationApplyChanges);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = _serviceProvider.CreateAsyncScope();
                var syncService = scope.ServiceProvider.GetRequiredService<IMeshCentralGroupPolicySyncService>();

                var report = await syncService.RunBackfillAsync(
                    applyChanges: _options.GroupPolicyReconciliationApplyChanges,
                    cancellationToken: stoppingToken);

                _logger.LogInformation(
                    "MeshCentral group policy reconciliation finished. ApplyChanges={ApplyChanges}, TotalSites={TotalSites}, UpdatedSites={UpdatedSites}, DriftedSites={DriftedSites}, FailedSites={FailedSites}, DurationMs={DurationMs}.",
                    report.ApplyChanges,
                    report.TotalSites,
                    report.UpdatedSites,
                    report.DriftedSites,
                    report.FailedSites,
                    (report.FinishedAtUtc - report.StartedAtUtc).TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MeshCentral group policy reconciliation cycle failed.");
            }
        }
    }
}

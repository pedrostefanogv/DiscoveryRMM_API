using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que reconcilia políticas de grupo MeshCentral com o banco local.
/// Substitui MeshCentralGroupPolicyReconciliationBackgroundService.
/// Schedule: configurável via MeshCentral:GroupPolicyReconciliationIntervalMinutes (default 60min).
/// </summary>
[DisallowConcurrentExecution]
public sealed class MeshCentralGroupPolicyReconciliationJob : IJob
{
    public static readonly JobKey Key = new("meshcentral-group-policy-reconciliation", "reconciliation");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var options = context.GetScopedService<IOptions<MeshCentralOptions>>().Value;
        var logger = context.GetLogger<MeshCentralGroupPolicyReconciliationJob>();
        var ct = context.CancellationToken;

        if (!options.Enabled || !options.GroupPolicyReconciliationEnabled)
        {
            logger.LogDebug("MeshCentral group policy reconciliation is disabled by configuration.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IMeshCentralGroupPolicySyncService>();

        try
        {
            var report = await syncService.RunBackfillAsync(
                applyChanges: options.GroupPolicyReconciliationApplyChanges,
                cancellationToken: ct);

            logger.LogInformation(
                "MeshCentral group policy reconciliation finished. ApplyChanges={ApplyChanges}, TotalSites={TotalSites}, UpdatedSites={UpdatedSites}, DriftedSites={DriftedSites}, FailedSites={FailedSites}, DurationMs={DurationMs}.",
                report.ApplyChanges, report.TotalSites, report.UpdatedSites,
                report.DriftedSites, report.FailedSites,
                (report.FinishedAtUtc - report.StartedAtUtc).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "MeshCentral group policy reconciliation cycle failed.");
        }
    }
}

using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job que reconcilia identidades de usuários MeshCentral com o banco local.
/// Substitui MeshCentralIdentityReconciliationBackgroundService.
/// Schedule: configurável via MeshCentral:IdentitySyncReconciliationIntervalMinutes (default 60min).
/// </summary>
[DisallowConcurrentExecution]
public sealed class MeshCentralIdentityReconciliationJob : IJob
{
    public static readonly JobKey Key = new("meshcentral-identity-reconciliation", "reconciliation");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var options = context.GetScopedService<IOptions<MeshCentralOptions>>().Value;
        var logger = context.GetLogger<MeshCentralIdentityReconciliationJob>();
        var ct = context.CancellationToken;

        if (!options.Enabled || !options.IdentitySyncEnabled || !options.IdentitySyncReconciliationEnabled)
        {
            logger.LogDebug("MeshCentral identity reconciliation is disabled by configuration.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var syncService = scope.ServiceProvider.GetRequiredService<IMeshCentralIdentitySyncService>();

        try
        {
            var report = await syncService.RunBackfillAsync(
                applyChanges: options.IdentitySyncReconciliationApplyChanges,
                cancellationToken: ct);

            logger.LogInformation(
                "MeshCentral identity reconciliation finished. ApplyChanges={ApplyChanges}, TotalUsers={TotalUsers}, SyncedUsers={SyncedUsers}, FailedUsers={FailedUsers}, DurationMs={DurationMs}.",
                report.ApplyChanges, report.TotalUsers, report.SyncedUsers,
                report.FailedUsers, (report.FinishedAtUtc - report.StartedAtUtc).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "MeshCentral identity reconciliation cycle failed.");
        }
    }
}

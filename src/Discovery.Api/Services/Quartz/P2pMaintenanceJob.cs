using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job that performs P2P maintenance:
/// - Cleans stale artifact presence records
/// - Recalculates seed plans for active sites
/// Replaces P2pMaintenanceBackgroundService.
/// Schedule: every 15 minutes (*/15 * * * *)
/// </summary>
[DisallowConcurrentExecution]
public sealed class P2pMaintenanceJob : IJob
{
    public static readonly JobKey Key = new("p2p-maintenance", "maintenance");
    private const int TelemetryActiveWindowMinutes = 10;
    private const int PresenceTtlHours = 2;
    private const int SeedPlanConfiguredPercent = 10;
    private const int SeedPlanMinSeeds = 2;

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<P2pMaintenanceJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var now = DateTime.UtcNow;
        var activeCutoff = now.AddMinutes(-TelemetryActiveWindowMinutes);
        var presenceCutoff = now.AddHours(-PresenceTtlHours);

        var deletedPresence = await db.P2pArtifactPresences
            .Where(p => p.LastSeenAt < presenceCutoff)
            .ExecuteDeleteAsync(ct);

        var activeBySite = await db.P2pAgentTelemetries
            .AsNoTracking()
            .Where(t => t.ReceivedAt >= activeCutoff)
            .GroupBy(t => new { t.SiteId, t.ClientId })
            .Select(g => new { g.Key.SiteId, g.Key.ClientId, TotalAgents = g.Select(x => x.AgentId).Distinct().Count() })
            .ToListAsync(ct);

        var existingPlans = await db.P2pSeedPlans.ToListAsync(ct);
        var activeMap = activeBySite.ToDictionary(x => x.SiteId, x => x);
        var existingSiteIds = existingPlans.Select(p => p.SiteId).ToHashSet();
        var updatedPlans = 0;
        var createdPlans = 0;

        foreach (var plan in existingPlans)
        {
            if (activeMap.TryGetValue(plan.SiteId, out var active))
            {
                plan.ClientId = active.ClientId;
                plan.TotalAgents = active.TotalAgents;
                plan.ConfiguredPercent = SeedPlanConfiguredPercent;
                plan.MinSeeds = SeedPlanMinSeeds;
                plan.SelectedSeeds = CalculateSelectedSeeds(active.TotalAgents);
                plan.GeneratedAt = now;
            }
            else
            {
                plan.TotalAgents = 0;
                plan.ConfiguredPercent = SeedPlanConfiguredPercent;
                plan.MinSeeds = SeedPlanMinSeeds;
                plan.SelectedSeeds = 0;
                plan.GeneratedAt = now;
            }
            updatedPlans++;
        }

        foreach (var active in activeBySite)
        {
            if (existingSiteIds.Contains(active.SiteId)) continue;
            db.P2pSeedPlans.Add(new P2pSeedPlan
            {
                SiteId = active.SiteId, ClientId = active.ClientId,
                TotalAgents = active.TotalAgents, ConfiguredPercent = SeedPlanConfiguredPercent,
                MinSeeds = SeedPlanMinSeeds, SelectedSeeds = CalculateSelectedSeeds(active.TotalAgents),
                GeneratedAt = now
            });
            createdPlans++;
        }

        if (updatedPlans > 0 || createdPlans > 0)
            await db.SaveChangesAsync(ct);

        logger.LogInformation("P2P maintenance: deleted {Presence} presence, updated {UpdatedPlans} plans, created {CreatedPlans}", deletedPresence, updatedPlans, createdPlans);
        context.Result = new { deletedPresence, updatedPlans, createdPlans };
    }

    private static int CalculateSelectedSeeds(int totalAgents) =>
        Math.Max(SeedPlanMinSeeds, (int)Math.Ceiling(totalAgents * SeedPlanConfiguredPercent / 100.0));
}

using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Services;

public class P2pMaintenanceBackgroundService : BackgroundService
{
    private const int TelemetryActiveWindowMinutes = 10;
    private const int PresenceTtlHours = 2;
    private const int SeedPlanConfiguredPercent = 10;
    private const int SeedPlanMinSeeds = 2;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<P2pMaintenanceBackgroundService> _logger;

    public P2pMaintenanceBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<P2pMaintenanceBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunMaintenanceOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunMaintenanceOnceAsync(stoppingToken);
        }
    }

    private async Task RunMaintenanceOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

            var now = DateTime.UtcNow;
            var activeCutoff = now.AddMinutes(-TelemetryActiveWindowMinutes);
            var presenceCutoff = now.AddHours(-PresenceTtlHours);

            var deletedPresence = await db.P2pArtifactPresences
                .Where(p => p.LastSeenAt < presenceCutoff)
                .ExecuteDeleteAsync(stoppingToken);

            var activeBySite = await db.P2pAgentTelemetries
                .AsNoTracking()
                .Where(t => t.ReceivedAt >= activeCutoff)
                .GroupBy(t => new { t.SiteId, t.ClientId })
                .Select(g => new
                {
                    g.Key.SiteId,
                    g.Key.ClientId,
                    TotalAgents = g.Select(x => x.AgentId).Distinct().Count()
                })
                .ToListAsync(stoppingToken);

            var existingPlans = await db.P2pSeedPlans.ToListAsync(stoppingToken);
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
                if (existingSiteIds.Contains(active.SiteId))
                    continue;

                db.P2pSeedPlans.Add(new P2pSeedPlan
                {
                    SiteId = active.SiteId,
                    ClientId = active.ClientId,
                    TotalAgents = active.TotalAgents,
                    ConfiguredPercent = SeedPlanConfiguredPercent,
                    MinSeeds = SeedPlanMinSeeds,
                    SelectedSeeds = CalculateSelectedSeeds(active.TotalAgents),
                    GeneratedAt = now
                });

                createdPlans++;
            }

            if (updatedPlans > 0 || createdPlans > 0)
                await db.SaveChangesAsync(stoppingToken);

            _logger.LogInformation(
                "P2P maintenance completed. Deleted presence: {DeletedPresence}, updated plans: {UpdatedPlans}, created plans: {CreatedPlans}",
                deletedPresence,
                updatedPlans,
                createdPlans);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "P2P maintenance failed.");
        }
    }

    private static int CalculateSelectedSeeds(int totalAgents)
    {
        if (totalAgents == 0)
            return 0;

        var fromPercent = (int)Math.Ceiling(totalAgents * SeedPlanConfiguredPercent / 100.0);
        var selected = Math.Max(fromPercent, SeedPlanMinSeeds);
        return Math.Min(selected, totalAgents);
    }
}
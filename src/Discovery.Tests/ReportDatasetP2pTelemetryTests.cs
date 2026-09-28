using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Discovery.Tests;

/// <summary>
/// O dataset p2pTelemetry expunha contadores CUMULATIVOS crus; qualquer SUM/AVG
/// do relatório somava o acumulado em vez do tráfego do período. Agora as colunas
/// de métrica trazem o incremento reset-aware e o valor cru fica em *Cumulative.
/// </summary>
public class ReportDatasetP2pTelemetryTests
{
    [Test]
    public async Task QueryAsync_CumulativeCountersExposePeriodIncrements()
    {
        await using var db = CreateDbContext();
        var (agentId, siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        db.P2pAgentTelemetries.AddRange(
            Snapshot(1, agentId, siteId, clientId, now.AddMinutes(-30), served: 500, downloaded: 100, started: 0, succeeded: 0, failed: 0),
            Snapshot(2, agentId, siteId, clientId, now.AddMinutes(-20), served: 900, downloaded: 400, started: 10, succeeded: 8, failed: 1),
            // Restart do agent: contadores zeram (novo segmento).
            Snapshot(3, agentId, siteId, clientId, now.AddMinutes(-10), served: 0, downloaded: 0, started: 0, succeeded: 0, failed: 0),
            Snapshot(4, agentId, siteId, clientId, now.AddMinutes(-5), served: 300, downloaded: 200, started: 5, succeeded: 3, failed: 1));
        await db.SaveChangesAsync();

        var service = new ReportDatasetQueryService(db, new MemoryCache(new MemoryCacheOptions()));
        var result = await service.QueryAsync(
            new ReportTemplate { DatasetType = ReportDatasetType.P2pTelemetry },
            """{"limit":100,"orderDirection":"asc"}""");

        var rows = result.Rows.ToList();
        Assert.That(rows, Has.Count.EqualTo(4));
        // Ordem asc por collectedAt.
        Assert.That(rows[0]["id"], Is.EqualTo(1L));
        // Primeira amostra do agente: baseline desconhecido → 0 (não infla o relatório).
        Assert.That(rows[0]["bytesServed"], Is.EqualTo(0L));
        // 0 + 400 + 0 (reset) + 300 = 700 — e NÃO o acumulado (900/300).
        Assert.That(rows.Sum(r => (long)r["bytesServed"]!), Is.EqualTo(700L));
        Assert.That(rows.Sum(r => (long)r["bytesDownloaded"]!), Is.EqualTo(500L));
        Assert.That(rows.Sum(r => (long)r["replicationsStarted"]!), Is.EqualTo(15L));
        Assert.That(rows.Sum(r => (long)r["replicationsSucceeded"]!), Is.EqualTo(11L));
        Assert.That(rows.Sum(r => (long)r["replicationsFailed"]!), Is.EqualTo(2L));
        // Valor cru permanece acessível explicitamente.
        Assert.That(rows[1]["bytesServedCumulative"], Is.EqualTo(900L));
        Assert.That(rows[1]["replicationsSucceededCumulative"], Is.EqualTo(8L));
    }

    [Test]
    public async Task QueryAsync_SingleSampleDoesNotCountUnknownBaseline()
    {
        await using var db = CreateDbContext();
        var (agentId, siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        db.P2pAgentTelemetries.Add(Snapshot(1, agentId, siteId, clientId, DateTime.UtcNow.AddMinutes(-1),
            served: 888_133_803, downloaded: 0, started: 3, succeeded: 2, failed: 1));
        await db.SaveChangesAsync();

        var service = new ReportDatasetQueryService(db, new MemoryCache(new MemoryCacheOptions()));
        var result = await service.QueryAsync(
            new ReportTemplate { DatasetType = ReportDatasetType.P2pTelemetry }, """{"limit":10}""");

        var row = result.Rows.Single();
        Assert.That(row["bytesServed"], Is.EqualTo(0L), "baseline desconhecido não pode virar tráfego do período");
        Assert.That(row["bytesServedCumulative"], Is.EqualTo(888_133_803L));
    }

    [Test]
    public void CounterDelta_HandlesGrowthResetAndUnknownBaseline()
    {
        Assert.That(ReportDatasetQueryService.CounterDelta(null, 500), Is.EqualTo(0));
        Assert.That(ReportDatasetQueryService.CounterDelta(500, 900), Is.EqualTo(400));
        Assert.That(ReportDatasetQueryService.CounterDelta(900, 900), Is.EqualTo(0));
        Assert.That(ReportDatasetQueryService.CounterDelta(900, 0), Is.EqualTo(0));
        Assert.That(ReportDatasetQueryService.CounterDelta(900, 300), Is.EqualTo(300));
    }

    private static P2pAgentTelemetry Snapshot(long id, Guid agentId, Guid siteId, Guid clientId, DateTime collectedAt,
        long served, long downloaded, long started, long succeeded, long failed) => new()
    {
        Id = id,
        AgentId = agentId,
        SiteId = siteId,
        ClientId = clientId,
        CollectedAt = collectedAt,
        ReceivedAt = collectedAt,
        BytesServed = served,
        BytesDownloaded = downloaded,
        ReplicationsStarted = started,
        ReplicationsSucceeded = succeeded,
        ReplicationsFailed = failed,
    };

    private static DiscoveryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"p2p-report-tests-{Guid.NewGuid():N}")
            .Options;
        return new P2pReportTestDbContext(options);
    }

    private sealed class P2pReportTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type> { typeof(P2pAgentTelemetry) };
            foreach (var entityType in typeof(P2pAgentTelemetry).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }
            modelBuilder.Entity<P2pAgentTelemetry>(entity => entity.HasKey(item => item.Id));
        }
    }
}

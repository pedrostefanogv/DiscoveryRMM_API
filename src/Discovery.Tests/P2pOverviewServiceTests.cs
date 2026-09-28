using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Testes de GetOverviewAsync (card "Métricas P2P" do dashboard).
/// Cobrem o estado "sem telemetria" e o cálculo de deltas/success rate que
/// alimentam os KPIs.
/// </summary>
public class P2pOverviewServiceTests
{
    [Test]
    public async Task GetOverviewAsync_WithoutSnapshots_ReportsNoData()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var result = await service.GetOverviewAsync("global", null, null, null, TimeSpan.FromHours(24));

        Assert.That(result.Health, Is.EqualTo("nodata"));
        Assert.That(result.Window, Is.EqualTo("24h"));
        Assert.That(result.Kpis.ActiveAgents, Is.Zero);
        Assert.That(result.Kpis.ActiveSeeders, Is.Zero);
        Assert.That(result.Kpis.ReplicationSuccessRate, Is.Zero);
        Assert.That(result.Kpis.LastTelemetryAtUtc, Is.Null);
    }

    [Test]
    public async Task GetOverviewAsync_ComputesDeltasAndSuccessRate()
    {
        await using var db = CreateDbContext();
        var (agentId, siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        db.P2pAgentTelemetries.AddRange(
            Snapshot(1, agentId, siteId, clientId, now.AddMinutes(-10),
                started: 0, succeeded: 0, served: 0, downloaded: 0, seeds: 1, queued: 0),
            Snapshot(2, agentId, siteId, clientId, now.AddMinutes(-5),
                started: 10, succeeded: 8, served: 1000, downloaded: 500, seeds: 1, queued: 0));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetOverviewAsync("global", null, null, null, TimeSpan.FromHours(24));

        Assert.That(result.Health, Is.EqualTo("ok"));
        Assert.That(result.Kpis.ActiveAgents, Is.EqualTo(1));
        Assert.That(result.Kpis.ActiveSeeders, Is.EqualTo(1));
        Assert.That(result.Kpis.ReplicationsStartedDelta, Is.EqualTo(10));
        Assert.That(result.Kpis.ReplicationsSucceededDelta, Is.EqualTo(8));
        Assert.That(result.Kpis.ReplicationSuccessRate, Is.EqualTo(80).Within(0.01));
        Assert.That(result.Kpis.BytesServedDelta, Is.EqualTo(1000));
        Assert.That(result.Kpis.BytesDownloadedDelta, Is.EqualTo(500));
        Assert.That(result.Kpis.LastTelemetryAtUtc, Is.Not.Null);
    }

    [Test]
    public async Task GetOverviewAsync_IgnoresSnapshotsOutsideWindow()
    {
        await using var db = CreateDbContext();
        var (agentId, siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        // ReceivedAt 30h atrás: fora da janela de 24h.
        db.P2pAgentTelemetries.Add(Snapshot(1, agentId, siteId, clientId, now.AddHours(-30),
            started: 0, succeeded: 0, served: 0, downloaded: 0, seeds: 1, queued: 0));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetOverviewAsync("global", null, null, null, TimeSpan.FromHours(24));

        Assert.That(result.Health, Is.EqualTo("nodata"));
        Assert.That(result.Kpis.ActiveAgents, Is.Zero);
    }

    [Test]
    public async Task GetOverviewAsync_WithoutReplications_DoesNotReportHundredPercent()
    {
        await using var db = CreateDbContext();
        var (agentId, siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        // Um único snapshot: sem delta de replicações → taxa não observável (0).
        db.P2pAgentTelemetries.Add(Snapshot(1, agentId, siteId, clientId, now.AddMinutes(-1),
            started: 0, succeeded: 0, served: 0, downloaded: 0, seeds: 0, queued: 0));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetOverviewAsync("global", null, null, null, TimeSpan.FromHours(24));

        Assert.That(result.Kpis.ReplicationsStartedDelta, Is.Zero);
        Assert.That(result.Kpis.ReplicationSuccessRate, Is.Zero);
        Assert.That(result.Kpis.ActiveAgents, Is.EqualTo(1));
    }

    [Test]
    public async Task GetOverviewAsync_WithoutReplications_KeepsHealthHealthy()
    {
        await using var db = CreateDbContext();
        var (agentId, siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        // Sem atividade de replicação (startedDelta == 0) e sem fila: a saúde NÃO
        // deve virar "critical" apenas por ausência de atividade.
        db.P2pAgentTelemetries.Add(Snapshot(1, agentId, siteId, clientId, now.AddMinutes(-1),
            started: 0, succeeded: 0, served: 0, downloaded: 0, seeds: 0, queued: 0));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetOverviewAsync("global", null, null, null, TimeSpan.FromHours(24));

        Assert.That(result.Kpis.ReplicationSuccessRate, Is.Zero);
        Assert.That(result.Health, Is.EqualTo("ok"));
    }

    [Test]
    public async Task GetAgentRankingAsync_ReportsRatesAsPercentages()
    {
        await using var db = CreateDbContext();
        var (agentId, siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        db.P2pAgentTelemetries.AddRange(
            Snapshot(1, agentId, siteId, clientId, now.AddMinutes(-10),
                started: 0, succeeded: 0, served: 0, downloaded: 0, seeds: 0, queued: 0, failed: 0),
            Snapshot(2, agentId, siteId, clientId, now.AddMinutes(-5),
                started: 10, succeeded: 8, served: 0, downloaded: 0, seeds: 0, queued: 0, failed: 2));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var ranking = await service.GetAgentRankingAsync("global", null, null, TimeSpan.FromHours(24), "peers");

        Assert.That(ranking, Has.Count.EqualTo(1));
        // Percentuais 0..100, consistentes com o overview.
        Assert.That(ranking[0].SuccessRate, Is.EqualTo(80).Within(0.01));
        Assert.That(ranking[0].FailureRate, Is.EqualTo(20).Within(0.01));
    }

    [Test]
    public async Task GetOverviewAsync_FormatsWindowAs30d()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var result = await service.GetOverviewAsync("global", null, null, null, TimeSpan.FromDays(30));

        Assert.That(result.Window, Is.EqualTo("30d"));
    }

    [Test]
    public async Task GetOverviewAsync_CountsArtifactsWithPeersWithinPresenceTtl()
    {
        await using var db = CreateDbContext();
        var (agentId, siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        db.P2pAgentTelemetries.Add(Snapshot(1, agentId, siteId, clientId, now.AddMinutes(-1),
            started: 0, succeeded: 0, served: 0, downloaded: 0, seeds: 0, queued: 0));
        db.P2pArtifactPresences.AddRange(
            new P2pArtifactPresence
            {
                ArtifactId = Guid.NewGuid(), AgentId = agentId, SiteId = siteId, ClientId = clientId,
                LastSeenAt = now.AddMinutes(-5)
            },
            new P2pArtifactPresence
            {
                ArtifactId = Guid.NewGuid(), AgentId = agentId, SiteId = siteId, ClientId = clientId,
                LastSeenAt = now.AddHours(-3) // fora do TTL de 2h
            });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetOverviewAsync("global", null, null, null, TimeSpan.FromHours(24));

        Assert.That(result.Kpis.ArtifactsWithPeers, Is.EqualTo(1));
    }

    [Test]
    public async Task GetOverviewAsync_AgentScope_CountsOnlyThatAgentsArtifacts()
    {
        await using var db = CreateDbContext();
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.P2pAgentTelemetries.AddRange(
            Snapshot(1, agentA, siteId, clientId, now.AddMinutes(-1), 0, 0, 0, 0, 0, 0),
            Snapshot(2, agentB, siteId, clientId, now.AddMinutes(-1), 0, 0, 0, 0, 0, 0));
        db.P2pArtifactPresences.AddRange(
            new P2pArtifactPresence
            {
                ArtifactId = Guid.NewGuid(), AgentId = agentA, SiteId = siteId, ClientId = clientId,
                LastSeenAt = now.AddMinutes(-5)
            },
            new P2pArtifactPresence
            {
                ArtifactId = Guid.NewGuid(), AgentId = agentB, SiteId = siteId, ClientId = clientId,
                LastSeenAt = now.AddMinutes(-5)
            });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetOverviewAsync("agent", null, null, agentA, TimeSpan.FromHours(24));

        Assert.That(result.Kpis.ActiveAgents, Is.EqualTo(1));
        // Antes o escopo "agent" não filtrava a presença e contava os 2 artefatos.
        Assert.That(result.Kpis.ArtifactsWithPeers, Is.EqualTo(1));
    }

    [Test]
    public async Task GetTimeseriesAsync_UsesServerReceivedTime()
    {
        await using var db = CreateDbContext();
        var (agentId, siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        // Coleta antiga (relógio do agente) mas recebida agora pelo servidor: a
        // janela da série é pelo received_at, então a amostra deve entrar.
        db.P2pAgentTelemetries.Add(Snapshot(1, agentId, siteId, clientId, now.AddHours(-30),
            started: 1, succeeded: 1, served: 500, downloaded: 0, seeds: 0, queued: 0,
            receivedAt: now.AddMinutes(-5)));
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var series = await service.GetTimeseriesAsync(
            "global", null, null, null, "bytesServed",
            now.AddHours(-1), now.AddMinutes(1), TimeSpan.FromMinutes(10));

        Assert.That(series.Summary.Total, Is.EqualTo(500));
    }

    [Test]
    public async Task UpsertArtifactPresenceAsync_DeduplicatesAndUpdatesExisting()
    {
        await using var db = CreateDbContext();
        var (agentId, siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var artifactA = Guid.NewGuid();
        var artifactB = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.P2pArtifactPresences.Add(new P2pArtifactPresence
        {
            ArtifactId = artifactA, AgentId = agentId, SiteId = siteId, ClientId = clientId,
            ArtifactName = "antigo", LastSeenAt = now.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.UpsertArtifactPresenceAsync(agentId, siteId, clientId,
        [
            new P2pArtifactPresenceDto { ArtifactId = artifactA.ToString(), ArtifactName = "novo" },
            // Duplicado no mesmo payload: antes gerava violação de PK.
            new P2pArtifactPresenceDto { ArtifactId = artifactA.ToString(), ArtifactName = "novo-2" },
            new P2pArtifactPresenceDto { ArtifactId = artifactB.ToString(), ArtifactName = "b" },
        ], CancellationToken.None);

        var rows = await db.P2pArtifactPresences.AsNoTracking()
            .Where(p => p.AgentId == agentId)
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2), "dedupe deve evitar inserir a mesma presença duas vezes");
        Assert.That(rows.Single(p => p.ArtifactId == artifactA).ArtifactName, Is.EqualTo("novo-2"));
        Assert.That(rows.Single(p => p.ArtifactId == artifactB).ArtifactName, Is.EqualTo("b"));
    }

    [Test]
    public async Task EnsureSeedPlanFreshAsync_RecalculatesWhenPlanIsEmpty()
    {
        await using var db = CreateDbContext();
        var (siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        db.P2pAgentTelemetries.AddRange(
            Snapshot(1, Guid.NewGuid(), siteId, clientId, now.AddMinutes(-1), 0, 0, 0, 0, 0, 0),
            Snapshot(2, Guid.NewGuid(), siteId, clientId, now.AddMinutes(-1), 0, 0, 0, 0, 0, 0));
        // Plano "zerado" como ficaria entre o boot dos agentes e o job de 15 min.
        db.P2pSeedPlans.Add(new P2pSeedPlan
        {
            SiteId = siteId, ClientId = clientId, TotalAgents = 0, SelectedSeeds = 0,
            GeneratedAt = now.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.EnsureSeedPlanFreshAsync(siteId, clientId, CancellationToken.None);

        var plan = await db.P2pSeedPlans.AsNoTracking().SingleAsync(p => p.SiteId == siteId);
        Assert.That(plan.TotalAgents, Is.EqualTo(2));
        Assert.That(plan.SelectedSeeds, Is.EqualTo(2));
    }

    [Test]
    public async Task EnsureSeedPlanFreshAsync_KeepsExistingPlanWhenNotEmpty()
    {
        await using var db = CreateDbContext();
        var (siteId, clientId) = (Guid.NewGuid(), Guid.NewGuid());
        var now = DateTime.UtcNow;

        db.P2pSeedPlans.Add(new P2pSeedPlan
        {
            SiteId = siteId, ClientId = clientId, TotalAgents = 7, SelectedSeeds = 7,
            GeneratedAt = now.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.EnsureSeedPlanFreshAsync(siteId, clientId, CancellationToken.None);

        var plan = await db.P2pSeedPlans.AsNoTracking().SingleAsync(p => p.SiteId == siteId);
        Assert.That(plan.TotalAgents, Is.EqualTo(7), "plano já populado não deve ser recalculado a cada ingest");
    }

    private static DiscoveryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"p2p-overview-tests-{Guid.NewGuid():N}")
            .Options;

        return new P2pOverviewTestDbContext(options);
    }

    // Repositórios/Redis não são usados por GetOverviewAsync.
    private static P2pService CreateService(DiscoveryDbContext db) => new(db, null!, null!, null!);

    private static P2pAgentTelemetry Snapshot(
        long id, Guid agentId, Guid siteId, Guid clientId, DateTime collectedAt,
        long started, long succeeded, long served, long downloaded, int seeds, int queued,
        long failed = 0, DateTime? receivedAt = null) => new()
    {
        Id = id,
        AgentId = agentId,
        SiteId = siteId,
        ClientId = clientId,
        CollectedAt = collectedAt,
        ReceivedAt = receivedAt ?? collectedAt,
        ReplicationsStarted = started,
        ReplicationsSucceeded = succeeded,
        ReplicationsFailed = failed,
        BytesServed = served,
        BytesDownloaded = downloaded,
        PlanSelectedSeeds = seeds,
        QueuedReplications = queued,
    };

    private sealed class P2pOverviewTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type>
            {
                typeof(P2pAgentTelemetry),
                typeof(P2pArtifactPresence),
                typeof(P2pSeedPlan)
            };

            foreach (var entityType in typeof(P2pAgentTelemetry).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<P2pAgentTelemetry>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<P2pArtifactPresence>(entity =>
                entity.HasKey(item => new { item.ArtifactId, item.AgentId }));
            modelBuilder.Entity<P2pSeedPlan>(entity => entity.HasKey(item => item.SiteId));
        }
    }
}

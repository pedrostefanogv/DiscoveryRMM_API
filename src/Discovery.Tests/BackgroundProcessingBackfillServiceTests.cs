using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Backfill de snapshots: pedido idempotente, execução em lotes com progresso,
/// conclusão (com limpeza opcional de órfãos), cancelamento e falha registrada.
/// </summary>
public class BackgroundProcessingBackfillServiceTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("backfill-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new BackfillTestDbContext(options);
    }

    private static BackgroundProcessingBackfillService BuildService(
        DiscoveryDbContext db, FakeMetrics metrics)
        => new(db, metrics, new FakeConfigResolver(), NullLogger<BackgroundProcessingBackfillService>.Instance);

    [Test]
    public async Task Request_IsIdempotentWhileRunning()
    {
        await using var db = CreateDb();
        var service = BuildService(db, new FakeMetrics());

        var first = await service.RequestAsync(null, false, "admin", CancellationToken.None);
        var second = await service.RequestAsync(null, false, "admin", CancellationToken.None);

        Assert.That(first.Status, Is.EqualTo(BackgroundProcessingBackfillService.StatusPending));
        Assert.That(second.Status, Is.EqualTo(first.Status));
        Assert.That(await db.ProcessingScopeStates.AsNoTracking()
            .CountAsync(s => s.ScopeType == BackgroundProcessingBackfillService.BackfillScopeType), Is.EqualTo(1));
    }

    [Test]
    public async Task ProcessNextBatch_TracksProgressUntilCompleted()
    {
        await using var db = CreateDb();
        var metrics = new FakeMetrics { Processed = 3, Total = 3, HasMore = true };
        var service = BuildService(db, metrics);

        await service.RequestAsync(null, false, "admin", CancellationToken.None);

        var running = await service.ProcessNextBatchAsync(CancellationToken.None);
        Assert.That(running!.Status, Is.EqualTo(BackgroundProcessingBackfillService.StatusRunning));
        Assert.That(running.Processed, Is.EqualTo(3));
        Assert.That(running.Total, Is.EqualTo(3));

        metrics.HasMore = false;
        var completed = await service.ProcessNextBatchAsync(CancellationToken.None);

        Assert.That(completed!.Status, Is.EqualTo(BackgroundProcessingBackfillService.StatusCompleted));
        Assert.That(completed.CompletedAt, Is.Not.Null);
    }

    [Test]
    public async Task ProcessNextBatch_PurgesOrphansOnlyForGlobalCompletion()
    {
        await using var db = CreateDb();
        var metrics = new FakeMetrics { Processed = 1, Total = 1, HasMore = false };
        var service = BuildService(db, metrics);

        await service.RequestAsync(null, purgeOrphans: true, "admin", CancellationToken.None);
        await service.ProcessNextBatchAsync(CancellationToken.None);

        Assert.That(metrics.PurgeCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Cancel_StopsPendingRequest()
    {
        await using var db = CreateDb();
        var service = BuildService(db, new FakeMetrics());

        var clientId = Guid.NewGuid();
        await service.RequestAsync(clientId, false, "admin", CancellationToken.None);

        var cancelled = await service.CancelAsync(clientId, "admin", CancellationToken.None);
        Assert.That(cancelled, Is.True);

        var state = await service.GetStateAsync(clientId, CancellationToken.None);
        Assert.That(state!.Status, Is.EqualTo(BackgroundProcessingBackfillService.StatusCancelled));

        var next = await service.ProcessNextBatchAsync(CancellationToken.None);
        Assert.That(next, Is.Null, "pedido cancelado não é processado");
    }

    [Test]
    public async Task ProcessNextBatch_OnFailure_RecordsError()
    {
        await using var db = CreateDb();
        var metrics = new FakeMetrics { ThrowOnRefresh = true };
        var service = BuildService(db, metrics);

        await service.RequestAsync(null, false, "admin", CancellationToken.None);
        var failed = await service.ProcessNextBatchAsync(CancellationToken.None);

        Assert.That(failed!.Status, Is.EqualTo(BackgroundProcessingBackfillService.StatusFailed));
        Assert.That(failed.LastError, Does.Contain("falha simulada"));
    }

    [Test]
    public async Task Request_PersistsCamelCasePayload_ThatTheUiCanRead()
    {
        await using var db = CreateDb();
        var service = BuildService(db, new FakeMetrics());

        await service.RequestAsync(null, false, "admin", CancellationToken.None);

        var row = await db.ProcessingScopeStates.AsNoTracking()
            .SingleAsync(s => s.ScopeType == BackgroundProcessingBackfillService.BackfillScopeType);

        Assert.That(row.LastResultJson, Does.Contain("\"status\""));
        Assert.That(row.LastResultJson, Does.Contain("\"requestedAt\""));
        Assert.That(row.LastResultJson, Does.Not.Contain("\"Status\""));
    }

    [Test]
    public async Task GetState_StillReadsLegacyPascalCasePayload()
    {
        await using var db = CreateDb();
        var service = BuildService(db, new FakeMetrics());

        await service.RequestAsync(null, false, "admin", CancellationToken.None);

        // A entidade já está rastreada pelo RequestAsync: atualiza a instância
        // rastreada em vez de reanexar outra (evita "já rastreada" no EF).
        var row = await db.ProcessingScopeStates
            .SingleAsync(s => s.ScopeType == BackgroundProcessingBackfillService.BackfillScopeType);
        row.LastResultJson = "{\"Status\":\"failed\",\"Total\":5,\"Processed\":2,\"LastError\":\"boom\"}";
        await db.SaveChangesAsync();

        var state = await service.GetStateAsync(null, CancellationToken.None);

        Assert.That(state, Is.Not.Null);
        Assert.That(state!.Status, Is.EqualTo(BackgroundProcessingBackfillService.StatusFailed));
        Assert.That(state.Total, Is.EqualTo(5));
        Assert.That(state.Processed, Is.EqualTo(2));
        Assert.That(state.LastError, Is.EqualTo("boom"));
    }

    private sealed class FakeMetrics : ITechnicianMetricsService
    {
        public int Processed { get; set; }
        public int Total { get; set; }
        public bool HasMore { get; set; }
        public bool ThrowOnRefresh { get; set; }
        public int PurgeCalls { get; private set; }

        public Task<MetricsBackfillProgress> RefreshForcedAsync(
            Guid? clientId, DateTime sessionStartUtc, int maxUsers, CancellationToken ct = default)
        {
            if (ThrowOnRefresh) throw new InvalidOperationException("falha simulada no backfill");
            return Task.FromResult(new MetricsBackfillProgress(Processed, Total, HasMore));
        }

        public Task<int> PurgeOrphanSnapshotsAsync(CancellationToken ct = default)
        {
            PurgeCalls++;
            return Task.FromResult(2);
        }

        public Task<TechnicianMetricsDto> GetMetricsAsync(Guid userId, Guid? clientScope = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TechnicianMetricsDto>> GetMetricsForUsersAsync(
            IReadOnlyCollection<Guid> userIds, Guid? clientScope = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> RefreshSnapshotsAsync(
            IReadOnlyCollection<Guid>? userIds = null, Guid? departmentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MetricsRefreshResult> RefreshDueAsync(CancellationToken ct = default, bool force = false)
            => throw new NotSupportedException();
    }

    private sealed class FakeConfigResolver : IConfigurationResolver
    {
        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(
            Guid? clientId, CancellationToken ct = default)
            => Task.FromResult(new BackgroundProcessingSettings());

        public Task<ServerConfiguration> GetServerAsync() => throw new NotSupportedException();
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<AutoUpdateSettings> GetAutoUpdateSettingsAsync(string level, Guid? targetId = null) => throw new NotSupportedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();
        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => Task.CompletedTask;
        public void ClearCache() { }
    }

    private sealed class BackfillTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Client), typeof(ProcessingScopeState) };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<ProcessingScopeState>(e => e.HasKey(s => s.Id));
        }
    }
}

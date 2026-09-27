using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Métricas por atendente: leitura SOMENTE por snapshot (sem recálculo de snapshot
/// vencido), bootstrap de quem nunca teve snapshot e ciclo periódico por escopo de
/// cliente com lotes, cota e vencimento. A agregação SQL é substituída por um fake
/// (mesma semântica) para os testes de regra.
/// </summary>
public class TechnicianMetricsServiceTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("technician-metrics-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new MetricsTestDbContext(options);
    }

    private static TechnicianMetricsService BuildService(
        DiscoveryDbContext db, BackgroundProcessingSettings? settings = null,
        Dictionary<Guid, BackgroundProcessingSettings>? byClient = null)
    {
        var config = new FakeConfigurationResolver(settings ?? new BackgroundProcessingSettings());
        if (byClient is not null)
        {
            foreach (var (clientId, value) in byClient)
                config.ByClient[clientId] = value;
        }

        return new TechnicianMetricsService(db, new FakeAggregation(db), config);
    }

    private static Ticket NewTicket(
        Guid userId, DateTime createdAt, DateTime? closedAt,
        DateTime? firstRespondedAt = null, bool slaBreached = false,
        int? rating = null, string? category = null, Guid? clientId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId ?? Guid.NewGuid(),
            WorkflowStateId = Guid.NewGuid(),
            Title = "Chamado",
            Description = "Descrição",
            Priority = TicketPriority.Medium,
            AssignedToUserId = userId,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            ClosedAt = closedAt,
            FirstRespondedAt = firstRespondedAt,
            SlaBreached = slaBreached,
            Rating = rating,
            Category = category
        };

    private static async Task<(Guid UserId, Department MemberDepartment)> SeedMemberAsync(
        DiscoveryDbContext db, Guid? clientId)
    {
        var now = DateTime.UtcNow;
        var user = new User { Id = Guid.NewGuid(), Login = "u", Email = "u@x.com", FullName = "User", IsActive = true };
        db.Users.Add(user);

        var department = new Department
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "Suporte",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Departments.Add(department);

        db.DepartmentMembers.Add(new DepartmentMember
        {
            Id = Guid.NewGuid(),
            DepartmentId = department.Id,
            UserId = user.Id,
            IsActive = true,
            CreatedAt = now
        });

        await db.SaveChangesAsync();
        return (user.Id, department);
    }

    [Test]
    public async Task GetMetricsAsync_BootstrapsMissingSnapshot_AndAggregates()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Tickets.AddRange(
            NewTicket(userId, now.AddDays(-10), now.AddDays(-9),
                firstRespondedAt: now.AddDays(-10).AddMinutes(30), rating: 4, category: "Rede"),
            NewTicket(userId, now.AddDays(-2), null, slaBreached: true, category: "Rede"));
        db.TicketActivityLogs.Add(new TicketActivityLog
        {
            Id = Guid.NewGuid(),
            TicketId = Guid.NewGuid(),
            Type = TicketActivityType.Reopened,
            CreatedAt = now.AddDays(-8)
        });
        await db.SaveChangesAsync();

        // O log de reabertura referencia um ticket próprio: cria um coerente.
        var reopenedTicket = await db.Tickets.AsNoTracking().FirstAsync();
        var log = await db.TicketActivityLogs.FirstAsync();
        log.TicketId = reopenedTicket.Id;
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var metrics = await service.GetMetricsAsync(userId);

        Assert.That(metrics.AssignedTotal, Is.EqualTo(2));
        Assert.That(metrics.ResolvedTotal, Is.EqualTo(1));
        Assert.That(metrics.OpenNow, Is.EqualTo(1));
        Assert.That(metrics.AvgFirstResponseMinutes, Is.EqualTo(30).Within(0.01));
        Assert.That(metrics.AvgResolutionMinutes, Is.EqualTo(24 * 60).Within(0.01));
        Assert.That(metrics.CsatAverage, Is.EqualTo(4).Within(0.01));
        Assert.That(metrics.ReopenRate, Is.EqualTo(1).Within(0.01));
        Assert.That(metrics.SlaBreachRate, Is.EqualTo(0.5).Within(0.01));
        Assert.That(metrics.TopCategories, Does.Contain("Rede"));
        Assert.That(metrics.ComputedAt, Is.Not.Null);
    }

    [Test]
    public async Task GetMetricsAsync_WithStaleSnapshot_DoesNotRecompute()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var staleAt = now.AddHours(-3);

        db.Tickets.Add(NewTicket(userId, now.AddDays(-1), null));
        db.TechnicianMetricsSnapshots.Add(new TechnicianMetricsSnapshot
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            WindowDays = 90,
            ComputedAt = staleAt,
            AssignedTotal = 7,
            ResolvedTotal = 5,
            OpenNow = 2,
            TopCategoriesJson = "[]",
            TopTagsJson = "[]"
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var metrics = await service.GetMetricsAsync(userId);

        Assert.That(metrics.AssignedTotal, Is.EqualTo(7), "snapshot vencido é usado como está");
        Assert.That(metrics.ComputedAt, Is.EqualTo(staleAt));

        var snapshot = await db.TechnicianMetricsSnapshots.AsNoTracking().SingleAsync();
        Assert.That(snapshot.ComputedAt, Is.EqualTo(staleAt), "nenhum recálculo dentro da requisição");
    }

    [Test]
    public async Task GetMetricsAsync_WithoutSnapshot_WithBootstrapOff_ReturnsEmptyWithoutPersisting()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        db.Tickets.Add(NewTicket(userId, DateTime.UtcNow.AddDays(-1), null));
        await db.SaveChangesAsync();

        var settings = new BackgroundProcessingSettings();
        settings.Metrics.BootstrapMissingSnapshots = false;

        var metrics = await BuildService(db, settings).GetMetricsAsync(userId);

        Assert.That(metrics.AssignedTotal, Is.EqualTo(0));
        Assert.That(metrics.ComputedAt, Is.Null, "ComputedAt null = nunca calculado");
        Assert.That(await db.TechnicianMetricsSnapshots.AsNoTracking().CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task RefreshDueAsync_ProcessesDueScopesAndSkipsThemBeforeTheInterval()
    {
        await using var db = CreateDb();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var (userA, _) = await SeedMemberAsync(db, clientA);
        var (userB, _) = await SeedMemberAsync(db, clientB);

        db.Tickets.AddRange(
            NewTicket(userA, DateTime.UtcNow.AddDays(-1), null, clientId: clientA),
            NewTicket(userB, DateTime.UtcNow.AddDays(-1), null, clientId: clientB));
        await db.SaveChangesAsync();

        var settings = new BackgroundProcessingSettings();
        settings.Metrics.IntervalMinutes = 10;
        settings.Metrics.StaleThresholdMinutes = 15;
        settings.Metrics.BatchSize = 5;
        settings.Metrics.MaxBatchesPerRun = 2;

        var service = BuildService(db, settings);

        var first = await service.RefreshDueAsync();
        Assert.That(first.ScopesProcessed, Is.EqualTo(2));
        Assert.That(first.UsersUpdated, Is.EqualTo(2));

        var second = await service.RefreshDueAsync();
        Assert.That(second.ScopesProcessed, Is.EqualTo(0), "escopos ainda não venceram");
        Assert.That(second.UsersUpdated, Is.EqualTo(0));

        Assert.That(await db.ProcessingScopeStates.AsNoTracking().CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task RefreshDueAsync_SkipsDisabledClientScope()
    {
        await using var db = CreateDb();
        var enabledClient = Guid.NewGuid();
        var disabledClient = Guid.NewGuid();
        var (userA, _) = await SeedMemberAsync(db, enabledClient);
        var (userB, _) = await SeedMemberAsync(db, disabledClient);

        db.Tickets.AddRange(
            NewTicket(userA, DateTime.UtcNow.AddDays(-1), null, clientId: enabledClient),
            NewTicket(userB, DateTime.UtcNow.AddDays(-1), null, clientId: disabledClient));
        await db.SaveChangesAsync();

        var enabled = new BackgroundProcessingSettings();
        var disabled = new BackgroundProcessingSettings();
        disabled.Metrics.Enabled = false;

        var service = BuildService(db, enabled, new Dictionary<Guid, BackgroundProcessingSettings>
        {
            [disabledClient] = disabled
        });

        var result = await service.RefreshDueAsync();

        Assert.That(result.ScopesProcessed, Is.EqualTo(1));
        Assert.That(result.UpdatedByClient.Keys, Is.EquivalentTo(new[] { enabledClient }));

        var snapshots = await db.TechnicianMetricsSnapshots.AsNoTracking().ToListAsync();
        Assert.That(snapshots.Any(s => s.UserId == userA), Is.True);
        Assert.That(snapshots.Any(s => s.UserId == userB), Is.False, "cliente desabilitado não é processado");
    }

    [Test]
    public async Task RefreshDueAsync_CapsBatchesPerRun_AndCoversTheRestOnTheNextCycle()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var (user1, department) = await SeedMemberAsync(db, clientId);
        var user2 = Guid.NewGuid();
        var user3 = Guid.NewGuid();

        db.Users.AddRange(
            new User { Id = user2, Login = "u2", Email = "u2@x.com", FullName = "U2", IsActive = true },
            new User { Id = user3, Login = "u3", Email = "u3@x.com", FullName = "U3", IsActive = true });
        db.DepartmentMembers.AddRange(
            new DepartmentMember { Id = Guid.NewGuid(), DepartmentId = department.Id, UserId = user2, IsActive = true, CreatedAt = DateTime.UtcNow },
            new DepartmentMember { Id = Guid.NewGuid(), DepartmentId = department.Id, UserId = user3, IsActive = true, CreatedAt = DateTime.UtcNow });

        db.Tickets.AddRange(
            NewTicket(user1, DateTime.UtcNow.AddDays(-1), null, clientId: clientId),
            NewTicket(user2, DateTime.UtcNow.AddDays(-1), null, clientId: clientId),
            NewTicket(user3, DateTime.UtcNow.AddDays(-1), null, clientId: clientId));
        await db.SaveChangesAsync();

        var settings = new BackgroundProcessingSettings();
        settings.Metrics.BatchSize = 1;
        settings.Metrics.MaxBatchesPerRun = 2;
        settings.Metrics.IntervalMinutes = 10;

        var service = BuildService(db, settings);

        var first = await service.RefreshDueAsync();
        Assert.That(first.UsersUpdated, Is.EqualTo(2), "teto de 2 lotes de 1 usuário");
        Assert.That(first.UsersPending, Is.EqualTo(1));

        // Próximo ciclo: libera o vencimento e o usuário que sobrou é o primeiro da fila.
        var state = await db.ProcessingScopeStates.FirstAsync();
        state.LastRunAt = DateTime.UtcNow.AddMinutes(-30);
        await db.SaveChangesAsync();

        var second = await service.RefreshDueAsync();
        Assert.That(second.UsersUpdated, Is.EqualTo(1));

        Assert.That(await db.TechnicianMetricsSnapshots.AsNoTracking().CountAsync(), Is.EqualTo(3));
    }

    [Test]
    public async Task RefreshForcedAsync_ForClientScope_OnlyTouchesThatClientsUsers()
    {
        await using var db = CreateDb();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var (userA, _) = await SeedMemberAsync(db, clientA);
        var (userB, _) = await SeedMemberAsync(db, clientB);

        db.Tickets.AddRange(
            NewTicket(userA, DateTime.UtcNow.AddDays(-1), null, clientId: clientA),
            NewTicket(userB, DateTime.UtcNow.AddDays(-1), null, clientId: clientB));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var progress = await service.RefreshForcedAsync(
            clientA, DateTime.UtcNow, maxUsers: 10, CancellationToken.None);

        Assert.That(progress.Total, Is.EqualTo(1));
        Assert.That(progress.HasMore, Is.False);

        var snapshots = await db.TechnicianMetricsSnapshots.AsNoTracking().ToListAsync();
        Assert.That(snapshots.Any(s => s.UserId == userA), Is.True);
        Assert.That(snapshots.Any(s => s.UserId == userB), Is.False, "escopo do backfill é respeitado");
    }

    [Test]
    public async Task RefreshSnapshotsAsync_DepartmentScope_OnlyTouchesDepartmentMembers()
    {
        await using var db = CreateDb();
        var (memberOfA, departmentA) = await SeedMemberAsync(db, clientId: Guid.NewGuid());
        var (memberOfB, _) = await SeedMemberAsync(db, clientId: Guid.NewGuid());

        // Responsável "global": tem chamado atribuído, mas NÃO é membro do departamento A.
        var globalAssignee = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = globalAssignee, Login = "global", Email = "g@x.com", FullName = "Global", IsActive = true
        });
        db.Tickets.Add(NewTicket(globalAssignee, DateTime.UtcNow.AddDays(-1), null));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var saved = await service.RefreshSnapshotsAsync(null, departmentA.Id, CancellationToken.None);

        Assert.That(saved, Is.EqualTo(1), "só o membro do departamento é recalculado");

        var targets = await db.TechnicianMetricsSnapshots.AsNoTracking()
            .Select(s => s.UserId).ToListAsync();
        Assert.That(targets, Does.Contain(memberOfA));
        Assert.That(targets, Does.Not.Contain(memberOfB), "membro de outro departamento fica intacto");
        Assert.That(targets, Does.Not.Contain(globalAssignee), "responsável global não entra no refresh do departamento");
    }

    [Test]
    public async Task PurgeOrphanSnapshotsAsync_RemovesOnlyUsersWithoutScope()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var (validUser, _) = await SeedMemberAsync(db, clientId);
        var orphanUser = Guid.NewGuid();

        db.Tickets.Add(NewTicket(validUser, DateTime.UtcNow.AddDays(-1), null, clientId: clientId));
        db.TechnicianMetricsSnapshots.AddRange(
            new TechnicianMetricsSnapshot
            {
                Id = Guid.NewGuid(),
                UserId = validUser,
                WindowDays = 90,
                ComputedAt = DateTime.UtcNow,
                TopCategoriesJson = "[]",
                TopTagsJson = "[]"
            },
            new TechnicianMetricsSnapshot
            {
                Id = Guid.NewGuid(),
                UserId = orphanUser,
                WindowDays = 90,
                ComputedAt = DateTime.UtcNow,
                TopCategoriesJson = "[]",
                TopTagsJson = "[]"
            });
        await db.SaveChangesAsync();

        var removed = await BuildService(db).PurgeOrphanSnapshotsAsync(CancellationToken.None);

        Assert.That(removed, Is.EqualTo(1));

        var remaining = await db.TechnicianMetricsSnapshots.AsNoTracking().ToListAsync();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].UserId, Is.EqualTo(validUser));
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    /// <summary>Porta da agregação (mesma semântica do SQL) para o provider InMemory.</summary>
    private sealed class FakeAggregation(DiscoveryDbContext db) : ITechnicianMetricsAggregationRepository
    {
        public async Task<IReadOnlyList<TechnicianMetricsAggregateRow>> GetAggregatesAsync(
            IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default)
        {
            var ids = userIds.ToList();
            var rows = await db.Tickets.AsNoTracking()
                .Where(t => t.DeletedAt == null && t.AssignedToUserId != null
                            && ids.Contains(t.AssignedToUserId!.Value)
                            && (t.CreatedAt >= since || t.ClosedAt == null))
                .Select(t => new { t.AssignedToUserId, t.CreatedAt, t.FirstRespondedAt, t.ClosedAt, t.SlaBreached, t.Rating })
                .ToListAsync(ct);

            var result = new List<TechnicianMetricsAggregateRow>();
            foreach (var group in rows.GroupBy(r => r.AssignedToUserId!.Value))
            {
                var window = group.Where(r => r.CreatedAt >= since).ToList();
                var resolved = window.Where(r => r.ClosedAt.HasValue).ToList();
                var frt = window
                    .Where(r => r.FirstRespondedAt.HasValue && r.FirstRespondedAt!.Value >= r.CreatedAt)
                    .Select(r => (r.FirstRespondedAt!.Value - r.CreatedAt).TotalMinutes)
                    .ToList();
                var resolution = resolved
                    .Where(r => r.ClosedAt!.Value >= r.CreatedAt)
                    .Select(r => (r.ClosedAt!.Value - r.CreatedAt).TotalMinutes)
                    .OrderBy(v => v)
                    .ToList();
                var ratings = window.Where(r => r.Rating.HasValue).Select(r => (double)r.Rating!.Value).ToList();

                result.Add(new TechnicianMetricsAggregateRow(
                    group.Key,
                    window.Count,
                    resolved.Count,
                    group.Count(r => !r.ClosedAt.HasValue),
                    frt.Count > 0 ? frt.Average() : null,
                    resolution.Count > 0 ? resolution.Average() : null,
                    resolution.Count > 0 ? Percentile(resolution, 0.90) : null,
                    window.Count(r => r.SlaBreached),
                    ratings.Count > 0 ? ratings.Average() : null,
                    ratings.Count));
            }

            return result;
        }

        public async Task<IReadOnlyList<TechnicianCategoryCount>> GetTopCategoriesAsync(
            IReadOnlyCollection<Guid> userIds, DateTime since, int perUser, CancellationToken ct = default)
        {
            var ids = userIds.ToList();
            var rows = await db.Tickets.AsNoTracking()
                .Where(t => t.DeletedAt == null && t.AssignedToUserId != null
                            && ids.Contains(t.AssignedToUserId!.Value)
                            && t.CreatedAt >= since
                            && t.Category != null && t.Category != "")
                .Select(t => new { t.AssignedToUserId, t.Category })
                .ToListAsync(ct);

            return rows
                .GroupBy(r => r.AssignedToUserId!.Value)
                .SelectMany(g => g.GroupBy(r => r.Category!)
                    .OrderByDescending(c => c.Count())
                    .ThenBy(c => c.Key)
                    .Take(perUser)
                    .Select(c => new TechnicianCategoryCount(g.Key, c.Key, c.Count())))
                .ToList();
        }

        public async Task<IReadOnlyDictionary<Guid, int>> GetReopenCountsAsync(
            IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default)
        {
            var ids = userIds.ToList();
            var rows = await db.TicketActivityLogs.AsNoTracking()
                .Where(l => l.Type == TicketActivityType.Reopened && l.CreatedAt >= since)
                .Join(db.Tickets.AsNoTracking().Where(t => t.DeletedAt == null && t.AssignedToUserId != null
                        && ids.Contains(t.AssignedToUserId!.Value)),
                    l => l.TicketId, t => t.Id, (_, t) => t.AssignedToUserId!.Value)
                .ToListAsync(ct);

            return rows.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        }

        public async Task<IReadOnlyDictionary<Guid, double>> GetDifficultyAveragesAsync(
            IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default)
        {
            var ids = userIds.ToList();
            var rows = await db.TicketAssignmentDecisions.AsNoTracking()
                .Where(d => d.Applied && d.ChosenUserId != null && ids.Contains(d.ChosenUserId!.Value)
                            && d.CreatedAt >= since)
                .Select(d => new { d.ChosenUserId, d.Difficulty })
                .ToListAsync(ct);

            return rows.GroupBy(r => r.ChosenUserId!.Value)
                .ToDictionary(g => g.Key, g => g.Average(r => (double)r.Difficulty));
        }

        private static double Percentile(IReadOnlyList<double> values, double percentile)
        {
            var index = (int)Math.Ceiling(percentile * values.Count) - 1;
            return values[Math.Clamp(index, 0, values.Count - 1)];
        }
    }

    private sealed class FakeConfigurationResolver(BackgroundProcessingSettings settings) : IConfigurationResolver
    {
        public BackgroundProcessingSettings Settings { get; set; } = settings;

        public Dictionary<Guid, BackgroundProcessingSettings> ByClient { get; } = [];

        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(
            Guid? clientId, CancellationToken ct = default)
            => Task.FromResult(
                clientId.HasValue && ByClient.TryGetValue(clientId.Value, out var perClient)
                    ? perClient
                    : Settings);

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

    private sealed class MetricsTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(User), typeof(Client), typeof(Ticket), typeof(TicketActivityLog),
                typeof(TechnicianMetricsSnapshot), typeof(TicketAssignmentDecision),
                typeof(Department), typeof(DepartmentMember), typeof(ProcessingScopeState)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<User>(e => e.HasKey(u => u.Id));
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<TicketActivityLog>(e => e.HasKey(l => l.Id));
            modelBuilder.Entity<TechnicianMetricsSnapshot>(e => e.HasKey(s => s.Id));
            modelBuilder.Entity<TicketAssignmentDecision>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<DepartmentMember>(e => e.HasKey(m => m.Id));
            modelBuilder.Entity<ProcessingScopeState>(e => e.HasKey(s => s.Id));
        }
    }
}

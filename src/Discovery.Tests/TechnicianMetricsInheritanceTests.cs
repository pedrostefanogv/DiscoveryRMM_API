using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// D8 — métricas por atendente incluem a EQUIPE EFETIVA (membros herdados do
/// departamento global) tanto no ciclo por cliente quanto no refresh do dept.
/// </summary>
public class TechnicianMetricsInheritanceTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("technician-metrics-inherit-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new MetricsInheritanceDbContext(options);
    }

    private static async Task<(Guid ClientDepartmentId, Guid InheritedUserId)> SeedAsync(
        DiscoveryDbContext db, bool ownMember = false)
    {
        var global = new Department
        {
            Id = Guid.NewGuid(), ClientId = null, Name = "Global",
            IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var client = new Department
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), Name = "Cliente",
            IsActive = true, InheritFromGlobalId = global.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var inheritedUser = new User
        {
            Id = Guid.NewGuid(), Login = "herdado", Email = "h@x.com",
            FullName = "Herdado", IsActive = true
        };
        var ownUser = new User
        {
            Id = Guid.NewGuid(), Login = "proprio", Email = "p@x.com",
            FullName = "Próprio", IsActive = true
        };

        db.Departments.AddRange(global, client);
        db.Users.AddRange(inheritedUser, ownUser);
        db.DepartmentMembers.Add(new DepartmentMember
        {
            Id = Guid.NewGuid(), DepartmentId = global.Id, UserId = inheritedUser.Id,
            IsActive = true, CreatedAt = DateTime.UtcNow
        });
        if (ownMember)
        {
            db.DepartmentMembers.Add(new DepartmentMember
            {
                Id = Guid.NewGuid(), DepartmentId = client.Id, UserId = ownUser.Id,
                IsActive = true, CreatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return (client.Id, inheritedUser.Id);
    }

    [Test]
    public async Task RefreshSnapshots_do_departamento_inclui_membros_herdados()
    {
        await using var db = CreateDb();
        var (departmentId, inheritedUserId) = await SeedAsync(db, ownMember: true);

        var service = new TechnicianMetricsService(db, new EmptyAggregation(), new FakeConfig());
        var saved = await service.RefreshSnapshotsAsync(null, departmentId, CancellationToken.None);

        Assert.That(saved, Is.EqualTo(2), "próprio + herdado");

        var userIds = await db.TechnicianMetricsSnapshots.AsNoTracking()
            .Select(s => s.UserId).ToListAsync();
        Assert.That(userIds, Does.Contain(inheritedUserId), "membro herdado entra no snapshot");
    }

    [Test]
    public async Task RefreshDue_por_cliente_inclui_membros_herdados()
    {
        await using var db = CreateDb();
        var (departmentId, inheritedUserId) = await SeedAsync(db);

        var clientId = await db.Departments.AsNoTracking()
            .Where(d => d.Id == departmentId)
            .Select(d => d.ClientId!.Value)
            .FirstAsync();

        var service = new TechnicianMetricsService(db, new EmptyAggregation(), new FakeConfig());
        // force: recalcula os dois escopos (global e cliente) no mesmo ciclo.
        var result = await service.RefreshDueAsync(force: true);

        Assert.That(result.ScopesProcessed, Is.GreaterThanOrEqualTo(2));
        Assert.That(result.UpdatedByClient.Keys, Does.Contain(clientId));

        var userIds = await db.TechnicianMetricsSnapshots.AsNoTracking()
            .Select(s => s.UserId).ToListAsync();
        Assert.That(userIds, Does.Contain(inheritedUserId));
    }

    private sealed class EmptyAggregation : ITechnicianMetricsAggregationRepository
    {
        public Task<IReadOnlyList<TechnicianMetricsAggregateRow>> GetAggregatesAsync(
            IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TechnicianMetricsAggregateRow>>([]);

        public Task<IReadOnlyList<TechnicianCategoryCount>> GetTopCategoriesAsync(
            IReadOnlyCollection<Guid> userIds, DateTime since, int perUser, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TechnicianCategoryCount>>([]);

        public Task<IReadOnlyDictionary<Guid, int>> GetReopenCountsAsync(
            IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>());

        public Task<IReadOnlyDictionary<Guid, double>> GetDifficultyAveragesAsync(
            IReadOnlyCollection<Guid> userIds, DateTime since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, double>>(new Dictionary<Guid, double>());
    }

    private sealed class FakeConfig : IConfigurationResolver
    {
        private readonly BackgroundProcessingSettings _settings = new();

        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(
            Guid? clientId, CancellationToken ct = default)
            => Task.FromResult(_settings);

        public Task<ServerConfiguration> GetServerAsync() => throw new NotSupportedException();
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();
        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => Task.CompletedTask;
        public void ClearCache() { }
    }

    private sealed class MetricsInheritanceDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(User), typeof(Department), typeof(DepartmentMember), typeof(Ticket),
                typeof(TechnicianMetricsSnapshot), typeof(ProcessingScopeState)
            };

            foreach (var entityType in typeof(Department).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<User>(e => e.HasKey(u => u.Id));
            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<DepartmentMember>(e => e.HasKey(m => m.Id));
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<TechnicianMetricsSnapshot>(e => e.HasKey(s => s.Id));
            modelBuilder.Entity<ProcessingScopeState>(e => e.HasKey(s => s.Id));
        }
    }
}

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
/// Auto-atribuição compartilhada na criação do chamado: usada pelo fluxo da API
/// e pelos chamados criados por alerta/evento.
/// </summary>
public class TicketAutoAssignmentServiceTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("auto-assign-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new AutoAssignTestDbContext(options);
    }

    private static async Task<(DiscoveryDbContext Db, Guid DepartmentId, Guid UserId, Ticket Ticket)> SeedAsync(
        TicketAssignmentStrategy strategy)
    {
        var db = CreateDb();
        var now = DateTime.UtcNow;

        var user = new User { Id = Guid.NewGuid(), Login = "ana", Email = "ana@x.com", FullName = "Ana", IsActive = true };
        db.Users.Add(user);

        var department = new Department
        {
            Id = Guid.NewGuid(),
            Name = "Suporte",
            AssignmentStrategy = (int)strategy,
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

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            WorkflowStateId = Guid.NewGuid(),
            DepartmentId = department.Id,
            Title = "Chamado",
            Description = "Descrição",
            Priority = TicketPriority.Medium,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        return (db, department.Id, user.Id, ticket);
    }

    private static TicketAutoAssignmentService BuildService(
        DiscoveryDbContext db, FakeQueue queue, BackgroundProcessingSettings? settings = null)
        => new(
            new TicketAssignmentService(db, new DepartmentTeamResolver(db)),
            queue,
            new FakeProcessingConfig(settings ?? new BackgroundProcessingSettings()),
            db);

    [Test]
    public async Task Resolve_KeepsExplicitAssignee()
    {
        var (db, departmentId, userId, _) = await SeedAsync(TicketAssignmentStrategy.RoundRobin);
        await using var _db = db;

        var resolution = await BuildService(db, new FakeQueue())
            .ResolveAsync(departmentId, userId, CancellationToken.None);

        Assert.That(resolution.AssignedToUserId, Is.EqualTo(userId));
        Assert.That(resolution.QueueAiTriage, Is.False);
    }

    [Test]
    public async Task Resolve_WithoutDepartment_DoesNothing()
    {
        var (db, _, _, _) = await SeedAsync(TicketAssignmentStrategy.RoundRobin);
        await using var _db = db;

        var resolution = await BuildService(db, new FakeQueue())
            .ResolveAsync(null, null, CancellationToken.None);

        Assert.That(resolution.AssignedToUserId, Is.Null);
        Assert.That(resolution.QueueAiTriage, Is.False);
    }

    [Test]
    public async Task ApplyAfterCreate_AssignsByRoundRobin()
    {
        var (db, _, userId, ticket) = await SeedAsync(TicketAssignmentStrategy.RoundRobin);
        await using var _db = db;

        var assigned = await BuildService(db, new FakeQueue())
            .ApplyAfterCreateAsync(ticket, CancellationToken.None);

        Assert.That(assigned, Is.True);

        var stored = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.That(stored.AssignedToUserId, Is.EqualTo(userId));
    }

    [Test]
    public async Task ApplyAfterCreate_WithAiTriage_EnqueuesAndDoesNotAssign()
    {
        var (db, departmentId, _, ticket) = await SeedAsync(TicketAssignmentStrategy.AiTriage);
        await using var _db = db;

        var queue = new FakeQueue();
        var applied = await BuildService(db, queue).ApplyAfterCreateAsync(ticket, CancellationToken.None);

        Assert.That(applied, Is.True);
        Assert.That(queue.Enqueued, Has.Count.EqualTo(1));
        Assert.That(queue.Enqueued[0].TicketId, Is.EqualTo(ticket.Id));
        Assert.That(queue.Enqueued[0].DepartmentId, Is.EqualTo(departmentId));

        var stored = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.That(stored.AssignedToUserId, Is.Null);
    }

    [Test]
    public async Task ApplyAfterCreate_WithBatchOnlyClient_DoesNotEnqueue()
    {
        var (db, departmentId, _, ticket) = await SeedAsync(TicketAssignmentStrategy.AiTriage);
        await using var _db = db;

        var settings = new BackgroundProcessingSettings();
        settings.Triage.EnqueueOnCreate = false;

        var queue = new FakeQueue();
        var applied = await BuildService(db, queue, settings).ApplyAfterCreateAsync(ticket, CancellationToken.None);

        Assert.That(applied, Is.False, "modo somente-lotes não enfileira na abertura");
        Assert.That(queue.Enqueued, Is.Empty);

        var resolution = await BuildService(db, queue, settings)
            .ResolveAsync(departmentId, null, CancellationToken.None);
        Assert.That(resolution.QueueAiTriage, Is.False);
        Assert.That(resolution.Strategy, Is.EqualTo((int)TicketAssignmentStrategy.AiTriage));
    }

    [Test]
    public async Task ApplyAfterCreate_WithNoneStrategy_DoesNothing()
    {
        var (db, _, _, ticket) = await SeedAsync(TicketAssignmentStrategy.None);
        await using var _db = db;

        var applied = await BuildService(db, new FakeQueue())
            .ApplyAfterCreateAsync(ticket, CancellationToken.None);

        Assert.That(applied, Is.False);
    }

    private sealed class FakeQueue : IAiAssignmentQueueRepository
    {
        public List<(Guid TicketId, Guid DepartmentId, string? Reason)> Enqueued { get; } = [];

        public Task EnqueueAsync(Guid ticketId, Guid departmentId, string? reason, CancellationToken ct = default)
        {
            Enqueued.Add((ticketId, departmentId, reason));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AiAssignmentQueueItem>> ClaimBatchAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AiAssignmentQueueItem>>([]);

        public Task<IReadOnlyList<Guid>> ListPendingClientScopesAsync(int maxScopes, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<AiAssignmentQueueItem>> ClaimBatchForClientAsync(
            Guid clientId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AiAssignmentQueueItem>>([]);

        public Task MarkDoneAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkFailedAsync(Guid id, string errorMessage, TimeSpan retryDelay, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkSkippedAsync(Guid id, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountOutstandingAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeProcessingConfig(BackgroundProcessingSettings settings) : IConfigurationResolver
    {
        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(
            Guid? clientId, CancellationToken ct = default)
            => Task.FromResult(settings);

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

    private sealed class AutoAssignTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(User), typeof(Client), typeof(Department), typeof(DepartmentMember), typeof(Ticket)
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
            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<DepartmentMember>(e => e.HasKey(m => m.Id));
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
        }
    }
}

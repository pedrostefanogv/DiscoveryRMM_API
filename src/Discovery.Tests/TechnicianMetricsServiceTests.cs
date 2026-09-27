using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Agregação das métricas por atendente usadas pela triagem por IA.
/// Usa DiscoveryDbContext em memória com um subconjunto de entidades.
/// </summary>
public class TechnicianMetricsServiceTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"technician-metrics-{Guid.NewGuid():N}")
            .Options;
        return new MetricsTestDbContext(options);
    }

    private static Ticket NewTicket(Guid userId, DateTime createdAt, DateTime? closedAt, DateTime? firstRespondedAt = null, bool slaBreached = false, int? rating = null, string? category = null)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
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

    [Test]
    public async Task GetMetricsAsync_AggregatesCountsTimesAndRates()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var closed = NewTicket(userId, now.AddDays(-10), now.AddDays(-9),
            firstRespondedAt: now.AddDays(-10).AddMinutes(30), rating: 4, category: "Rede");
        var open = NewTicket(userId, now.AddDays(-2), null, slaBreached: true, category: "Rede");

        db.Tickets.AddRange(closed, open);
        db.TicketActivityLogs.Add(new TicketActivityLog
        {
            Id = Guid.NewGuid(),
            TicketId = closed.Id,
            Type = TicketActivityType.Reopened,
            CreatedAt = now.AddDays(-8)
        });
        await db.SaveChangesAsync();

        var service = new TechnicianMetricsService(db);
        var metrics = await service.GetMetricsAsync(userId);

        Assert.That(metrics.AssignedTotal, Is.EqualTo(2));
        Assert.That(metrics.ResolvedTotal, Is.EqualTo(1));
        Assert.That(metrics.OpenNow, Is.EqualTo(1));
        Assert.That(metrics.AvgFirstResponseMinutes, Is.EqualTo(30).Within(0.01));
        Assert.That(metrics.AvgResolutionMinutes, Is.EqualTo(24 * 60).Within(0.01));
        Assert.That(metrics.CsatAverage, Is.EqualTo(4).Within(0.01));
        Assert.That(metrics.CsatRatedCount, Is.EqualTo(1));
        Assert.That(metrics.ReopenRate, Is.EqualTo(1).Within(0.01));
        Assert.That(metrics.SlaBreachRate, Is.EqualTo(0.5).Within(0.01));
        Assert.That(metrics.TopCategories, Does.Contain("Rede"));
    }

    [Test]
    public async Task GetMetricsAsync_PersistsSnapshot_AndReusesIt()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        db.Tickets.Add(NewTicket(userId, DateTime.UtcNow.AddDays(-1), null));
        await db.SaveChangesAsync();

        var service = new TechnicianMetricsService(db);
        await service.GetMetricsAsync(userId);

        var stored = await db.TechnicianMetricsSnapshots.AsNoTracking().CountAsync();
        Assert.That(stored, Is.EqualTo(1));

        // Segunda leitura usa o snapshot (nenhuma linha nova).
        await service.GetMetricsAsync(userId);
        stored = await db.TechnicianMetricsSnapshots.AsNoTracking().CountAsync();
        Assert.That(stored, Is.EqualTo(1));
    }

    [Test]
    public async Task GetMetricsAsync_ComputesDifficultyAverage_FromAppliedDecisions()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Tickets.Add(NewTicket(userId, now.AddDays(-1), null));
        db.TicketAssignmentDecisions.Add(new TicketAssignmentDecision
        {
            Id = Guid.NewGuid(),
            TicketId = Guid.NewGuid(),
            DepartmentId = Guid.NewGuid(),
            Mode = 1,
            StrategySource = AiAssignmentDecisionSource.Ai,
            ChosenUserId = userId,
            Difficulty = 5,
            Applied = true,
            CreatedAt = now.AddDays(-1)
        });
        db.TicketAssignmentDecisions.Add(new TicketAssignmentDecision
        {
            Id = Guid.NewGuid(),
            TicketId = Guid.NewGuid(),
            DepartmentId = Guid.NewGuid(),
            Mode = 1,
            StrategySource = AiAssignmentDecisionSource.Ai,
            ChosenUserId = userId,
            Difficulty = 3,
            Applied = true,
            CreatedAt = now.AddDays(-2)
        });
        await db.SaveChangesAsync();

        var metrics = await new TechnicianMetricsService(db).GetMetricsAsync(userId);

        Assert.That(metrics.DifficultyAverage, Is.EqualTo(4.0).Within(0.01));
    }

    [Test]
    public async Task GetMetricsForUsersAsync_ReturnsEmptyMetrics_ForUserWithoutTickets()
    {
        await using var db = CreateDb();
        var userId = Guid.NewGuid();

        var service = new TechnicianMetricsService(db);
        var metrics = await service.GetMetricsForUsersAsync([userId]);

        Assert.That(metrics, Has.Count.EqualTo(1));
        Assert.That(metrics[0].AssignedTotal, Is.EqualTo(0));
        Assert.That(metrics[0].OpenNow, Is.EqualTo(0));
    }

    private sealed class MetricsTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(User), typeof(Client), typeof(Ticket), typeof(TicketActivityLog),
                typeof(TechnicianMetricsSnapshot), typeof(TicketAssignmentDecision)
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
        }
    }
}

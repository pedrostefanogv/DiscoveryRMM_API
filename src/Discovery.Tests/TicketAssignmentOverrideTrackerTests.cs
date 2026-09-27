using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Fechamento do loop de feedback: trocar o responsável após uma decisão da IA
/// marca a decisão como sobreposta (insumo da recalibração de pesos).
/// </summary>
public class TicketAssignmentOverrideTrackerTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("override-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new OverrideTestDbContext(options);
    }

    private static async Task<(DiscoveryDbContext Db, Guid TicketId, Guid AiUser, TicketAssignmentDecision Decision)> SeedAsync(
        bool applied = true)
    {
        var db = CreateDb();
        var ticketId = Guid.NewGuid();
        var aiUser = Guid.NewGuid();

        var decision = new TicketAssignmentDecision
        {
            Id = Guid.NewGuid(),
            TicketId = ticketId,
            DepartmentId = Guid.NewGuid(),
            Mode = 1,
            StrategySource = AiAssignmentDecisionSource.Ai,
            ChosenUserId = aiUser,
            Applied = applied,
            CreatedAt = DateTime.UtcNow
        };
        db.TicketAssignmentDecisions.Add(decision);
        await db.SaveChangesAsync();

        return (db, ticketId, aiUser, decision);
    }

    [Test]
    public async Task MarksDecision_WhenHumanChangesAssignee()
    {
        var (db, ticketId, aiUser, _) = await SeedAsync();
        await using var _db = db;
        var tracker = new TicketAssignmentOverrideTracker(db);
        var human = Guid.NewGuid();

        var marked = await tracker.MarkIfOverriddenAsync(ticketId, human, human, CancellationToken.None);

        Assert.That(marked, Is.True);
        var decision = await db.TicketAssignmentDecisions.AsNoTracking().SingleAsync();
        Assert.That(decision.OverriddenAt, Is.Not.Null);
        Assert.That(decision.OverriddenByUserId, Is.EqualTo(human));
        Assert.That(decision.ChosenUserId, Is.EqualTo(aiUser));
    }

    [Test]
    public async Task DoesNotMark_WhenAssigneeIsTheAiChoice()
    {
        var (db, ticketId, aiUser, _) = await SeedAsync();
        await using var _db = db;

        var marked = await new TicketAssignmentOverrideTracker(db)
            .MarkIfOverriddenAsync(ticketId, aiUser, Guid.NewGuid(), CancellationToken.None);

        Assert.That(marked, Is.False);
        var decision = await db.TicketAssignmentDecisions.AsNoTracking().SingleAsync();
        Assert.That(decision.OverriddenAt, Is.Null);
    }

    [Test]
    public async Task MarksOnlyOnce()
    {
        var (db, ticketId, _, _) = await SeedAsync();
        await using var _db = db;
        var tracker = new TicketAssignmentOverrideTracker(db);

        var first = await tracker.MarkIfOverriddenAsync(ticketId, Guid.NewGuid(), null, CancellationToken.None);
        var second = await tracker.MarkIfOverriddenAsync(ticketId, Guid.NewGuid(), null, CancellationToken.None);

        Assert.That(first, Is.True);
        Assert.That(second, Is.False);
    }

    [Test]
    public async Task DoesNotMarkDecisionThatWasNotApplied()
    {
        var (db, ticketId, _, _) = await SeedAsync(applied: false);
        await using var _db = db;

        var marked = await new TicketAssignmentOverrideTracker(db)
            .MarkIfOverriddenAsync(ticketId, Guid.NewGuid(), null, CancellationToken.None);

        Assert.That(marked, Is.False);
    }

    private sealed class OverrideTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Client), typeof(Ticket), typeof(TicketAssignmentDecision) };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<TicketAssignmentDecision>(e => e.HasKey(d => d.Id));
        }
    }
}

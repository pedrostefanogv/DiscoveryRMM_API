using Discovery.Core.Cqrs.Support.Csat;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Cqrs.Support;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Resumo de CSAT: garante o recorte por maquina (hostname do agent),
/// necessario porque a avaliacao feita na aba Suporte do agent grava
/// RatedBy = hostname e nao entra no agrupamento por tecnico.
/// </summary>
public class TicketCsatSummaryTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"csat-summary-{Guid.NewGuid():N}")
            .Options;
        return new CsatTestDbContext(options);
    }

    private static Ticket NewRatedTicket(Guid clientId, Guid departmentId, Guid? agentId, int rating)
    {
        var now = DateTime.UtcNow;
        return new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            DepartmentId = departmentId,
            AgentId = agentId,
            Title = "Chamado",
            Description = "Descricao",
            WorkflowStateId = Guid.NewGuid(),
            Priority = TicketPriority.Medium,
            Rating = rating,
            RatedAt = now,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now,
            ClosedAt = now
        };
    }

    [Test]
    public async Task CsatSummary_GroupsRatedTicketsByAgentHostname()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var department = new Department { Id = Guid.NewGuid(), Name = "TI", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var agentA = new Agent { Id = Guid.NewGuid(), SiteId = Guid.NewGuid(), Hostname = "PC-01" };
        var agentB = new Agent { Id = Guid.NewGuid(), SiteId = Guid.NewGuid(), Hostname = "PC-02", DisplayName = "Recepcao" };

        db.AddRange(department, agentA, agentB);
        db.AddRange(
            NewRatedTicket(clientId, department.Id, agentA.Id, 5),
            NewRatedTicket(clientId, department.Id, agentA.Id, 3),
            NewRatedTicket(clientId, department.Id, agentB.Id, 4),
            NewRatedTicket(clientId, department.Id, null, 2));
        await db.SaveChangesAsync();

        var handler = new GetTicketCsatSummaryQueryHandler(db);
        var result = await handler.Handle(new GetTicketCsatSummaryQuery(), default);

        Assert.That(result.IsSuccess, Is.True);
        var dto = result.Value!;
        Assert.That(dto.Rated, Is.EqualTo(4));

        var pc01 = dto.ByHostname.SingleOrDefault(g => g.Label == "PC-01");
        Assert.That(pc01, Is.Not.Null, "hostname PC-01 deve aparecer no recorte por maquina");
        Assert.That(pc01!.Count, Is.EqualTo(2));
        Assert.That(pc01.Average, Is.EqualTo(4d));
        Assert.That(pc01.Id, Is.EqualTo(agentA.Id.ToString()));

        var display = dto.ByHostname.SingleOrDefault(g => g.Label == "Recepcao");
        Assert.That(display, Is.Not.Null, "DisplayName deve ter prioridade sobre Hostname");
        Assert.That(display!.Average, Is.EqualTo(4d));

        var noMachine = dto.ByHostname.SingleOrDefault(g => g.Label == "Sem máquina");
        Assert.That(noMachine, Is.Not.Null, "chamado sem agent entra como Sem maquina");
        Assert.That(noMachine!.Count, Is.EqualTo(1));
    }

    private sealed class CsatTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client), typeof(Department), typeof(User), typeof(Agent), typeof(Ticket)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<User>(e => e.HasKey(u => u.Id));
            modelBuilder.Entity<Agent>(e => e.HasKey(a => a.Id));
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
        }
    }
}

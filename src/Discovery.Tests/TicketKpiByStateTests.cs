using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// KPI: distribuição por estado de workflow e FRT com a pausa de SLA efetiva.
/// Usa um contexto restrito (o DiscoveryDbContext completo não roda no InMemory).
/// </summary>
public class TicketKpiByStateTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"kpi-by-state-{Guid.NewGuid():N}")
            .Options;
        return new KpiTestDbContext(options);
    }

    private static Ticket NewTicket(Guid clientId, Guid stateId)
    {
        var now = DateTime.UtcNow;
        return new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Title = "Chamado",
            Description = "Descrição",
            WorkflowStateId = stateId,
            Priority = TicketPriority.Medium,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    [Test]
    public async Task GetKpiAsync_ByState_AggregatesWithSameFilter()
    {
        await using var db = CreateDb();
        var client = Guid.NewGuid();
        var stateA = Guid.NewGuid();
        var stateB = Guid.NewGuid();
        var otherClient = Guid.NewGuid();

        db.Tickets.AddRange(
            NewTicket(client, stateA),
            NewTicket(client, stateA),
            NewTicket(client, stateB),
            NewTicket(otherClient, stateA));
        await db.SaveChangesAsync();

        var repo = new TicketRepository(db, new KpiNullAgentMessaging());
        var kpi = await repo.GetKpiAsync(new TicketFilterQuery(ClientId: client));

        Assert.That(kpi.ByState, Is.Not.Null);
        Assert.That(kpi.ByState!.Count, Is.EqualTo(2));
        Assert.That(kpi.ByState!.Single(x => x.WorkflowStateId == stateA).Count, Is.EqualTo(2));
        Assert.That(kpi.ByState!.Single(x => x.WorkflowStateId == stateB).Count, Is.EqualTo(1));
    }

    [Test]
    public async Task GetKpiAsync_FrtAchieved_UsesEffectiveExpiryWithPause()
    {
        await using var db = CreateDb();
        var client = Guid.NewGuid();
        var state = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var originalExpiry = now.AddMinutes(-10);

        var ticket = NewTicket(client, state);
        ticket.SlaFirstResponseExpiresAt = originalExpiry;
        ticket.SlaPausedSeconds = 1800; // 30 min de pausa estendem o prazo
        // Respondeu 5 min depois do prazo ORIGINAL, mas dentro do prazo efetivo.
        ticket.FirstRespondedAt = originalExpiry.AddMinutes(5);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var repo = new TicketRepository(db, new KpiNullAgentMessaging());
        var kpi = await repo.GetKpiAsync(new TicketFilterQuery(ClientId: client));

        Assert.That(kpi.FrtAchievementRate, Is.EqualTo(100.0));
    }

    private sealed class KpiTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // TicketAnswer não é mapeado: o filtro de respostas é nulo no KPI do
            // teste e o tipo carrega um Vector (pgvector) que o InMemory não suporta.
            var allowed = new HashSet<Type> { typeof(Ticket) };

            foreach (var entityType in typeof(Ticket).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
        }
    }

    private sealed class KpiNullAgentMessaging : IAgentMessaging
    {
        public bool IsConnected => false;
        public Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload) => Task.CompletedTask;
        public Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, Guid overrideClientId, Guid overrideSiteId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishRemoteDebugControlAsync(Guid clientId, Guid siteId, Guid agentId, string payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendCommandToSubjectAsync(Guid clientId, Guid siteId, Guid agentId, Guid commandId, string commandType, string payload) => Task.CompletedTask;
        public Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

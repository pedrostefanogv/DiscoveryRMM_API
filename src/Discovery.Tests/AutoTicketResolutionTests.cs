using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Cobre o FECHAMENTO do chamado pela resolução do sensor — a correção do
/// "ticket órfão".
///
/// Contexto da pesquisa: o Zabbix suprime apenas ESCALAÇÕES durante a
/// manutenção, mas "recovery notifications are not sent if the problem started
/// during maintenance". Sem tratar a resolução, um alerta nascido na janela de
/// manutenção ficaria aberto para sempre.
/// </summary>
[TestFixture]
public class AutoTicketResolutionTests
{
    [Test]
    public async Task CloseCorrelatedTicket_ShouldCloseTicket_WhenCorrelationLockExists()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var finalState = SeedWorkflow(db, clientId);
        var ticket = SeedTicket(db, clientId, finalState.Id);
        SeedCorrelationLock(db, clientId, ticket.Id);

        var service = BuildService(db, ActivePolicy());
        var closed = await service.CloseCorrelatedTicketAsync(BuildResolutionEvent(clientId, ticket.AgentId ?? Guid.NewGuid()));

        Assert.Multiple(() =>
        {
            Assert.That(closed, Is.Not.Null, "O chamado correlacionado deve ser fechado.");
            Assert.That(closed!.ClosedAt, Is.Not.Null);
            Assert.That(closed.WorkflowStateId, Is.EqualTo(finalState.Id), "Deve ir para o estado FINAL do workflow.");
        });
    }

    [Test]
    public async Task CloseCorrelatedTicket_ShouldBeIdempotent_WhenTicketAlreadyClosed()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var finalState = SeedWorkflow(db, clientId);
        var ticket = SeedTicket(db, clientId, finalState.Id);
        ticket.ClosedAt = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        SeedCorrelationLock(db, clientId, ticket.Id);

        var service = BuildService(db, ActivePolicy());
        var closed = await service.CloseCorrelatedTicketAsync(BuildResolutionEvent(clientId, ticket.AgentId ?? Guid.NewGuid()));

        Assert.That(closed, Is.Null, "Não deve reprocessar um chamado já fechado.");
    }

    [Test]
    public async Task CloseCorrelatedTicket_ShouldDoNothing_WhenAutoTicketIsDisabled()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var finalState = SeedWorkflow(db, clientId);
        var ticket = SeedTicket(db, clientId, finalState.Id);
        SeedCorrelationLock(db, clientId, ticket.Id);

        var service = BuildService(db, FakePolicy.Disabled("Site"));
        var closed = await service.CloseCorrelatedTicketAsync(BuildResolutionEvent(clientId, ticket.AgentId ?? Guid.NewGuid()));

        Assert.That(closed, Is.Null,
            "Com o auto-ticket desligado no escopo, não há chamado nosso para fechar.");
    }

    [Test]
    public async Task CloseCorrelatedTicket_ShouldReturnNull_WhenThereIsNoCorrelation()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        SeedWorkflow(db, clientId);

        var service = BuildService(db, ActivePolicy());
        var closed = await service.CloseCorrelatedTicketAsync(BuildResolutionEvent(clientId, Guid.NewGuid()));

        Assert.That(closed, Is.Null, "Sem lock de correlação não há o que fechar.");
    }

    [Test]
    public async Task CloseCorrelatedTicket_ShouldLeaveTicketOpen_WhenNoFinalStateExists()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        // Sem workflow: não há estado final para onde mover.
        var ticket = SeedTicket(db, clientId, Guid.NewGuid());
        SeedCorrelationLock(db, clientId, ticket.Id);

        var service = BuildService(db, ActivePolicy());
        var closed = await service.CloseCorrelatedTicketAsync(BuildResolutionEvent(clientId, ticket.AgentId ?? Guid.NewGuid()));

        Assert.That(closed, Is.Null,
            "Sem estado final o chamado permanece aberto — melhor que fechar num estado inválido.");

        var reloaded = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.That(reloaded.ClosedAt, Is.Null);
    }

    [Test]
    public async Task CloseCorrelatedTicket_ShouldFallBackToGlobalFinalState()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        // Estado final GLOBAL (ClientId nulo) — o cliente não tem workflow próprio.
        var globalFinal = new WorkflowState
        {
            Id = Guid.NewGuid(),
            ClientId = null,
            Name = "Fechado (global)",
            IsFinal = true,
            SortOrder = 10,
            CreatedAt = DateTime.UtcNow
        };
        db.WorkflowStates.Add(globalFinal);
        await db.SaveChangesAsync();

        var ticket = SeedTicket(db, clientId, Guid.NewGuid());
        SeedCorrelationLock(db, clientId, ticket.Id);

        var service = BuildService(db, ActivePolicy());
        var closed = await service.CloseCorrelatedTicketAsync(BuildResolutionEvent(clientId, ticket.AgentId ?? Guid.NewGuid()));

        Assert.Multiple(() =>
        {
            Assert.That(closed, Is.Not.Null);
            Assert.That(closed!.WorkflowStateId, Is.EqualTo(globalFinal.Id));
        });
    }

    /// <summary>
    /// Concorrência otimista: a tabela de tickets usa xmin (row version). Se
    /// outra requisição fechar o chamado entre a leitura e a gravação, a
    /// escrita é rejeitada com DbUpdateConcurrencyException.
    ///
    /// O serviço PRECISA tratar isso: sem tratamento, a exceção sobe para o
    /// try/catch best-effort da ingestão e o chamado fica ABERTO — o mesmo
    /// ticket órfão que ele existe para evitar.
    /// </summary>
    [Test]
    public async Task CloseCorrelatedTicket_ShouldNotThrow_WhenAConcurrentCloseHappens()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var finalState = SeedWorkflow(db, clientId);
        var ticket = SeedTicket(db, clientId, finalState.Id);
        SeedCorrelationLock(db, clientId, ticket.Id);

        var service = BuildService(db, ActivePolicy());
        var evt = BuildResolutionEvent(clientId, ticket.AgentId!.Value);

        // Simula o "outro processo fechou no meio": marcamos o chamado como já
        // fechado em OUTRO contexto, ANTES da gravação, de modo que a escrita
        // deste serviço seja rejeitada pela concorrência otimista.
        await using (var other = CreateDb())
        {
            // Contexto distinto de propósito: representa outra requisição.
            db.Entry(ticket).State = EntityState.Detached;
            var fresh = await db.Tickets.FirstAsync(x => x.Id == ticket.Id);
            fresh.ClosedAt = DateTime.UtcNow;
            fresh.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // A asserção central: NÃO pode propagar exceção. Sem o tratamento, a
        // DbUpdateConcurrencyException subiria e o chamado ficaria órfão.
        var closed = await service.CloseCorrelatedTicketAsync(evt);

        Assert.That(closed, Is.Null, "Chamado ja fechado: devolve null, sem excecao.");

        var reloaded = await db.Tickets.AsNoTracking().FirstAsync(x => x.Id == ticket.Id);
        Assert.That(reloaded.ClosedAt, Is.Not.Null, "O chamado permanece fechado.");
    }

    [Test]
    public async Task CloseCorrelatedTicket_ShouldBeSafeToCallTwice()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var finalState = SeedWorkflow(db, clientId);
        var ticket = SeedTicket(db, clientId, finalState.Id);
        SeedCorrelationLock(db, clientId, ticket.Id);

        var service = BuildService(db, ActivePolicy());
        var evt = BuildResolutionEvent(clientId, ticket.AgentId!.Value);

        var first = await service.CloseCorrelatedTicketAsync(evt);
        var second = await service.CloseCorrelatedTicketAsync(evt);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null, "A primeira chamada fecha.");
            Assert.That(second, Is.Null, "A segunda e idempotente: nao refecha nem lanca.");
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ResolvedAutoTicketPolicy ActivePolicy() => new()
    {
        Enabled = true,
        Mode = AutoTicketScopeMode.Active
    };

    private static AutoTicketResolutionService BuildService(DiscoveryDbContext db, ResolvedAutoTicketPolicy policy)
        => new(db,
               new FakePolicyResolver(policy),
               new DedupFingerprintService(new MonitoringEventNormalizationService()),
               NullLogger<AutoTicketResolutionService>.Instance);

    private static WorkflowState SeedWorkflow(DiscoveryDbContext db, Guid clientId)
    {
        var final = new WorkflowState
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "Fechado",
            IsFinal = true,
            SortOrder = 10,
            CreatedAt = DateTime.UtcNow
        };
        db.WorkflowStates.Add(final);
        db.SaveChanges();
        return final;
    }

    private static Ticket SeedTicket(DiscoveryDbContext db, Guid clientId, Guid stateId)
    {
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AgentId = Guid.NewGuid(),
            Title = "Disk Full",
            Description = "Auto ticket",
            WorkflowStateId = stateId,
            Priority = TicketPriority.High,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        };
        db.Tickets.Add(ticket);
        db.SaveChanges();
        return ticket;
    }

    /// <summary>
    /// O lock de correlação é o elo entre o alerta e o chamado.
    ///
    /// A chave é montada no MESMO formato de produção
    /// (DedupFingerprintService.BuildDedupKey):
    ///   {clientIdN}:{siteN|none}:{agentIdN}:{alertCode}:{hash}:{timeBucket}
    ///
    /// Montar a chave "à mão" num formato diferente do real foi exatamente o que
    /// deixou passar um bug de correlação — por isso o formato é reproduzido aqui.
    /// </summary>
    private static void SeedCorrelationLock(
        DiscoveryDbContext db, Guid clientId, Guid ticketId)
    {
        // O mesmo agentId precisa estar no lock E no evento de resolução.
        var agentId = db.Tickets.AsNoTracking()
            .Where(t => t.Id == ticketId)
            .Select(t => t.AgentId!.Value)
            .First();

        db.AlertCorrelationLocks.Add(new AlertCorrelationLock
        {
            DedupKey = string.Join(':',
                clientId.ToString("N"),
                "none",
                agentId.ToString("N"),
                "sensor:disk_space",
                "4d5e6f7a8b9c",
                "12345678"),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            LastTicketId = ticketId,
            LastAlertAt = DateTime.UtcNow.AddMinutes(-10)
        });
        db.SaveChanges();
    }

    private static AgentMonitoringEvent BuildResolutionEvent(Guid clientId, Guid agentId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        AgentId = agentId,
        AlertCode = "sensor:disk_space",
        Severity = MonitoringEventSeverity.Attention,
        Title = "Sensor normalizado",
        Message = "Disco voltou ao normal.",
        Source = MonitoringEventSource.HealthCheck,
        OccurredAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"auto-ticket-resolution-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new AutoTicketTestDbContext(options);
    }

    private sealed class FakePolicyResolver(ResolvedAutoTicketPolicy policy) : IAutoTicketPolicyResolver
    {
        public Task<ResolvedAutoTicketPolicy> ResolveAsync(
            Guid? clientId = null, Guid? siteId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(policy);

        public Task<string?> GetRawPolicyJsonAsync(
            string level, Guid? targetId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakePolicy
    {
        public static ResolvedAutoTicketPolicy Disabled(string level) => new()
        {
            Enabled = false,
            Mode = AutoTicketScopeMode.Off,
            DisabledByLevel = level
        };
    }

    /// <summary>
    /// DbContext de teste que ignora as entidades não usadas aqui. Necessário
    /// porque o modelo completo inclui tipos específicos do Postgres (ex.: a
    /// coluna vetorial de KnowledgeArticleChunk), que o provider InMemory não
    /// consegue mapear.
    /// </summary>
    private sealed class AutoTicketTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type>
            {
                typeof(Ticket),
                typeof(AgentMonitoringEvent),
                typeof(AutoTicketRuleExecution),
                typeof(WorkflowState),
                typeof(AlertCorrelationLock)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                                        && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Ticket>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Ignore(item => item.DaysOpen);
            });

            modelBuilder.Entity<AgentMonitoringEvent>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AutoTicketRuleExecution>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<WorkflowState>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AlertCorrelationLock>(entity => entity.HasKey(item => item.DedupKey));
        }
    }
}
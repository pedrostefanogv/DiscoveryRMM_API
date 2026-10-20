using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Tickets.Chat;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

/// <summary>
/// Conversa de suporte AVULSA (sem chamado), aberta pela lista/detalhe do agent.
///
/// Cobre o que o recurso novo exige: sessão criada com ticket_id NULO, convite
/// despachado SEM ticketId (a FK opcional é o que permite a conversa existir) e
/// as regras de máquina online / uma sessão por máquina.
/// </summary>
[TestFixture]
public class SupportChatAdHocSessionTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid SiteId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private static DiscoveryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"ad-hoc-support-chat-{Guid.NewGuid():N}")
            .Options;
        return new SupportChatTestDbContext(options);
    }

    /// <summary>
    /// Contexto reduzido: o modelo completo não valida no provider InMemory.
    /// Só o que o handler usa (agent/site/cliente + sessões de chat).
    /// </summary>
    private sealed class SupportChatTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Só as entidades usadas aqui: sem isso o modelo completo traz tipos
            // do provider (pgvector) que não existem no InMemory.
            var allowed = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(Ticket),
                typeof(TicketChatSession)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Ticket>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Agent>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<TicketChatSession>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Status).HasConversion<string>();
                entity.Ignore(item => item.Messages);
                entity.HasOne(item => item.Ticket).WithMany().HasForeignKey(item => item.TicketId).IsRequired(false);
                entity.HasOne(item => item.Agent).WithMany().HasForeignKey(item => item.AgentId);
            });
        }
    }

    private static async Task<Agent> SeedAgentAsync(DiscoveryDbContext db, AgentStatus status)
    {
        db.Clients.Add(new Client { Id = ClientId, Name = "Cliente", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.Sites.Add(new Site { Id = SiteId, ClientId = ClientId, Name = "Matriz", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = SiteId,
            Hostname = "PC-01",
            DisplayName = "PC-01",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private static StartAgentSupportChatSessionCommandHandler CreateHandler(
        DiscoveryDbContext db,
        CapturingDispatcher dispatcher,
        RecordingPublisher publisher)
        => new(
            new TicketChatRepository(db),
            new AgentRepository(db),
            new SiteRepository(db),
            dispatcher,
            publisher,
            Options.Create(new TicketChatOptions()),
            NullLogger<StartAgentSupportChatSessionCommandHandler>.Instance);

    [Test]
    public async Task StartAgentSupportChatSession_ShouldCreateTicketlessSessionAndInviteWithoutTicketId()
    {
        using var db = CreateDbContext();
        var agent = await SeedAgentAsync(db, AgentStatus.Online);
        var dispatcher = new CapturingDispatcher();
        var publisher = new RecordingPublisher();

        var result = await CreateHandler(db, dispatcher, publisher)
            .Handle(new StartAgentSupportChatSessionCommand(agent.Id, null, "Ana"), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, result.IsFailure ? result.Errors[0].Message : null);
        var created = result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(created.TicketId, Is.Null, "conversa avulsa não pode ter chamado");
            Assert.That(created.AgentId, Is.EqualTo(agent.Id));
            Assert.That(created.Status, Is.EqualTo("Pending"));
        });

        var stored = await new TicketChatRepository(db).GetCurrentForAgentAsync(agent.Id);
        Assert.That(stored, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.TicketId, Is.Null);
            Assert.That(stored.ClientId, Is.EqualTo(ClientId), "tenant derivado do site da máquina");
            Assert.That(stored.SiteId, Is.EqualTo(SiteId));
        });

        Assert.That(dispatcher.LastCommand, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.LastCommand!.AgentId, Is.EqualTo(agent.Id));
            Assert.That(dispatcher.LastCommand.CommandType, Is.EqualTo(CommandType.TicketChatInvite));
            // Sem chamado: o campo nem é enviado (agent antigo lê string vazia).
            Assert.That(dispatcher.LastCommand.Payload, Does.Not.Contain("ticketId"));
            Assert.That(dispatcher.LastCommand.Payload, Does.Contain(created.Id.ToString()));
        });
        Assert.That(publisher.StatusSignals, Is.EqualTo(1), "console precisa do sinal de estado");
    }

    [Test]
    public async Task StartAgentSupportChatSession_ShouldReuseCurrentSessionForTheMachine()
    {
        using var db = CreateDbContext();
        var agent = await SeedAgentAsync(db, AgentStatus.Online);
        var dispatcher = new CapturingDispatcher();
        var publisher = new RecordingPublisher();
        var handler = CreateHandler(db, dispatcher, publisher);

        var first = await handler.Handle(new StartAgentSupportChatSessionCommand(agent.Id, null, "Ana"), CancellationToken.None);
        var second = await handler.Handle(new StartAgentSupportChatSessionCommand(agent.Id, null, "Bia"), CancellationToken.None);

        Assert.That(first.IsSuccess && second.IsSuccess, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(second.Value!.Id, Is.EqualTo(first.Value!.Id), "uma sessão por máquina");
            Assert.That(db.TicketChatSessions.Count(), Is.EqualTo(1));
            Assert.That(dispatcher.Dispatched.Count, Is.EqualTo(1), "não dispara convite duplicado");
        });
    }

    [Test]
    public async Task StartAgentSupportChatSession_ShouldRejectOfflineAgent()
    {
        using var db = CreateDbContext();
        var agent = await SeedAgentAsync(db, AgentStatus.Offline);

        var result = await CreateHandler(db, new CapturingDispatcher(), new RecordingPublisher())
            .Handle(new StartAgentSupportChatSessionCommand(agent.Id, null, "Ana"), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.Errors[0].Code, Is.EqualTo("Conflict"));
            Assert.That(result.Errors[0].Message, Is.EqualTo("agent_offline"));
            Assert.That(db.TicketChatSessions.Count(), Is.EqualTo(0));
        });
    }

    private sealed class CapturingDispatcher : IAgentCommandDispatcher
    {
        public List<AgentCommand> Dispatched { get; } = [];
        public AgentCommand? LastCommand => Dispatched.Count == 0 ? null : Dispatched[^1];

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            Dispatched.Add(command);
            return Task.FromResult(command);
        }
    }

    private sealed class RecordingPublisher : ITicketChatEnvelopePublisher
    {
        public int StatusSignals { get; private set; }
        public List<TicketChatSessionStatus> DeclinedPublishes { get; } = [];

        public Task PublishSessionOpenedAsync(TicketChatSession session, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishMessageToAgentAsync(TicketChatSession session, TicketChatMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishSessionDeclinedAsync(TicketChatSession session, CancellationToken cancellationToken = default)
        {
            DeclinedPublishes.Add(session.Status);
            return Task.CompletedTask;
        }

        public Task PublishSessionEndedAsync(TicketChatSession session, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishActivitySignalAsync(TicketChatSession session, TicketChatMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishSessionStatusSignalAsync(TicketChatSession session, CancellationToken cancellationToken = default)
        {
            StatusSignals++;
            return Task.CompletedTask;
        }
    }
}

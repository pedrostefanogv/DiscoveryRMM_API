using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Tickets.Chat;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// BUG DO "AGORA NÃO" — lado servidor.
///
/// Sintoma: o usuário clicava "Agora não" no cartão nativo, o agent logava
/// "reportado ao servidor", e o atendente continuava preso em "Aguardando
/// confirmação". Duas causas no servidor:
///
/// 1. o handler de resposta devolvia <c>Result.Success</c> (200 OK) quando a
///    sessão já não estava Pending — a recusa era ENGOLIDA;
/// 2. as consultas de sessão filtravam Pending/Active, então quando a sessão
///    virava Declined a consulta devolvia hasSession=false e o painel congelava
///    com o último snapshot "Pending".
///
/// Estes testes travam o contrato novo: recusa fora de Pending = 409, a consulta
/// devolve o desfecho (Declined/Expired/Ended) e o console continua distinto de
/// "nunca teve sessão".
/// </summary>
[TestFixture]
public class TicketChatInviteResponseTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid SiteId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private static DiscoveryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"chat-invite-response-{Guid.NewGuid():N}")
            .Options;
        return new ChatTestDbContext(options);
    }

    /// <summary>
    /// Contexto reduzido (mesmo padrão de SupportChatAdHocSessionTests): o modelo
    /// completo não valida no provider InMemory.
    /// </summary>
    private sealed class ChatTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
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

    private static async Task<(DiscoveryDbContext Db, Agent Agent)> SeedAsync(
        TicketChatSessionStatus status,
        DateTime? inviteExpiresAt = null,
        Guid? startedByUserId = null)
    {
        var db = CreateDbContext();

        db.Clients.Add(new Client { Id = ClientId, Name = "Cliente", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.Sites.Add(new Site { Id = SiteId, ClientId = ClientId, Name = "Matriz", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = SiteId,
            Hostname = "PC-01",
            DisplayName = "PC-01",
            Status = AgentStatus.Online,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        db.TicketChatSessions.Add(new TicketChatSession
        {
            Id = Guid.NewGuid(),
            TicketId = null,
            AgentId = agent.Id,
            ClientId = ClientId,
            SiteId = SiteId,
            Status = status,
            StartedByUserId = startedByUserId,
            StartedAt = now,
            InviteExpiresAt = inviteExpiresAt ?? now.AddSeconds(120),
            AcceptedAt = status == TicketChatSessionStatus.Active ? now : null,
            DeclinedAt = status == TicketChatSessionStatus.Declined ? now : null,
            DeclinedReason = status == TicketChatSessionStatus.Declined ? "user_denied" : null,
            EndedAt = status == TicketChatSessionStatus.Ended ? now : null,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        return (db, agent);
    }

    private static RespondTicketChatInviteCommandHandler CreateHandler(DiscoveryDbContext db, RecordingPublisher publisher)
        => new(
            new TicketChatRepository(db),
            publisher,
            new NoopActivityLog(),
            NullLogger<RespondTicketChatInviteCommandHandler>.Instance);

    [Test]
    public async Task DeclinedSession_RepeatedDeclineIsConflictNotSilentSuccess()
    {
        // Caso real: o usuário clicou "Agora não", a sessão virou Declined e uma
        // segunda resposta (reentrega do cartão) era respondida com 200 OK.
        var (db, agent) = await SeedAsync(TicketChatSessionStatus.Declined);
        using (db)
        {
            var session = await db.TicketChatSessions.SingleAsync();
            var publisher = new RecordingPublisher();

            var result = await CreateHandler(db, publisher)
                .Handle(new RespondTicketChatInviteCommand(session.Id, agent.Id, Accepted: false, Reason: "user_denied"), CancellationToken.None);

            Assert.That(result.IsFailure, Is.True, "recusa em sessão já Declined NÃO pode ser 200 OK");
            Assert.Multiple(() =>
            {
                Assert.That(result.Errors[0].Code, Is.EqualTo("Conflict"), "o controller do agent mapeia Conflict para HTTP 409");
                Assert.That(result.Errors[0].Message, Is.EqualTo("session_declined"));
            });
        }
    }

    [TestCase(TicketChatSessionStatus.Active, "session_active")]
    [TestCase(TicketChatSessionStatus.Expired, "session_expired")]
    [TestCase(TicketChatSessionStatus.Ended, "session_ended")]
    public async Task NonPendingSession_InviteResponseIsRejectedWithStatusCode(
        TicketChatSessionStatus status, string expectedCode)
    {
        var (db, agent) = await SeedAsync(status);
        using (db)
        {
            var session = await db.TicketChatSessions.SingleAsync();

            var result = await CreateHandler(db, new RecordingPublisher())
                .Handle(new RespondTicketChatInviteCommand(session.Id, agent.Id, Accepted: false, Reason: "user_denied"), CancellationToken.None);

            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Errors[0].Message, Is.EqualTo(expectedCode));
        }
    }

    [Test]
    public async Task PendingSession_IsDeclinedPersistedAndPublishedToAgent()
    {
        var (db, agent) = await SeedAsync(TicketChatSessionStatus.Pending);
        using (db)
        {
            var session = await db.TicketChatSessions.SingleAsync();
            var publisher = new RecordingPublisher();

            var result = await CreateHandler(db, publisher)
                .Handle(new RespondTicketChatInviteCommand(session.Id, agent.Id, Accepted: false, Reason: "user_denied"), CancellationToken.None);

            Assert.That(result.IsSuccess, Is.True, result.IsFailure ? result.Errors[0].Message : null);
            Assert.That(result.Value!.Status, Is.EqualTo("Declined"));

            var stored = await new TicketChatRepository(db).GetByIdAsync(session.Id);
            Assert.That(stored!.Status, Is.EqualTo(TicketChatSessionStatus.Declined));

            Assert.Multiple(() =>
            {
                // Envelope durável rumo ao agent (o sinal de dashboard não chega lá).
                Assert.That(publisher.DeclinedPublishes, Is.EqualTo(new[] { TicketChatSessionStatus.Declined }));
                // Console continua recebendo o sinal de estado.
                Assert.That(publisher.StatusSignals, Is.EqualTo(1));
            });
        }
    }

    [Test]
    public async Task LatestForAgent_ReturnsDeclinedSessionInsteadOfNothing()
    {
        var (db, agent) = await SeedAsync(TicketChatSessionStatus.Declined);
        using (db)
        {
            var repository = new TicketChatRepository(db);

            // Consulta de "ocupado": nada vivo.
            Assert.That(await repository.GetCurrentForAgentAsync(agent.Id), Is.Null);

            // Consulta de painel: o DESFECHO precisa aparecer; senão o console
            // congela no último snapshot "Pending".
            var latest = await repository.GetLatestForAgentAsync(agent.Id);
            Assert.That(latest, Is.Not.Null);
            Assert.That(latest!.Status, Is.EqualTo(TicketChatSessionStatus.Declined));
        }
    }

    [Test]
    public async Task LatestForAgent_ReturnsMostRecentSessionRegardlessOfStatus()
    {
        var (db, agent) = await SeedAsync(TicketChatSessionStatus.Pending);
        using (db)
        {
            var now = DateTime.UtcNow;
            db.TicketChatSessions.Add(new TicketChatSession
            {
                Id = Guid.NewGuid(),
                AgentId = agent.Id,
                ClientId = ClientId,
                SiteId = SiteId,
                Status = TicketChatSessionStatus.Ended,
                StartedAt = now.AddMinutes(5),
                EndedAt = now.AddMinutes(6),
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();

            var latest = await new TicketChatRepository(db).GetLatestForAgentAsync(agent.Id);
            Assert.That(latest!.Status, Is.EqualTo(TicketChatSessionStatus.Ended), "maior StartedAt vence, qualquer status");
        }
    }

    [Test]
    public async Task LatestForAgent_ReturnsNullOnlyWhenMachineNeverHadASession()
    {
        var (db, _) = await SeedAsync(TicketChatSessionStatus.Pending);
        using (db)
        {
            var latest = await new TicketChatRepository(db).GetLatestForAgentAsync(Guid.NewGuid());
            Assert.That(latest, Is.Null, "HasSession=false só quando não existe sessão nenhuma");
        }
    }

    [Test]
    public async Task StartAgentSession_CreatesNewSessionAfterDeclined()
    {
        var (db, agent) = await SeedAsync(TicketChatSessionStatus.Declined);
        using (db)
        {
            var declined = await db.TicketChatSessions.SingleAsync();
            var dispatcher = new CapturingDispatcher();

            var result = await new StartAgentSupportChatSessionCommandHandler(
                    new TicketChatRepository(db),
                    new AgentRepository(db),
                    new SiteRepository(db),
                    dispatcher,
                    new RecordingPublisher(),
                    new FallbackNameResolver(),
                    Microsoft.Extensions.Options.Options.Create(new Discovery.Core.Configuration.TicketChatOptions()),
                    NullLogger<StartAgentSupportChatSessionCommandHandler>.Instance)
                .Handle(new StartAgentSupportChatSessionCommand(agent.Id, null, "Ana"), CancellationToken.None);

            Assert.That(result.IsSuccess, Is.True, result.IsFailure ? result.Errors[0].Message : null);
            Assert.Multiple(() =>
            {
                Assert.That(result.Value!.Id, Is.Not.EqualTo(declined.Id), "sessão recusada NÃO pode ser reaproveitada");
                Assert.That(result.Value.Status, Is.EqualTo("Pending"));
                Assert.That(db.TicketChatSessions.Count(), Is.EqualTo(2));
                Assert.That(dispatcher.Dispatched, Has.Count.EqualTo(1), "nova conversa dispara novo convite");
            });
        }
    }

    [Test]
    public async Task StartAgentSession_StillReusesPendingSessionForTheSameSupportUser()
    {
        // A sessão pertence ao atendente (a conversa avulsa é privada de quem a
        // iniciou); o MESMO usuário reabre e reusa a sessão viva.
        var userId = Guid.NewGuid();
        var (db, agent) = await SeedAsync(TicketChatSessionStatus.Pending, startedByUserId: userId);
        using (db)
        {
            var pending = await db.TicketChatSessions.SingleAsync();

            var result = await new StartAgentSupportChatSessionCommandHandler(
                    new TicketChatRepository(db),
                    new AgentRepository(db),
                    new SiteRepository(db),
                    new CapturingDispatcher(),
                    new RecordingPublisher(),
                    new FallbackNameResolver(),
                    Microsoft.Extensions.Options.Options.Create(new Discovery.Core.Configuration.TicketChatOptions()),
                    NullLogger<StartAgentSupportChatSessionCommandHandler>.Instance)
                .Handle(new StartAgentSupportChatSessionCommand(agent.Id, userId, "Ana"), CancellationToken.None);

            Assert.That(result.Value!.Id, Is.EqualTo(pending.Id), "sessão viva do MESMO atendente continua sendo reaproveitada");
            Assert.That(db.TicketChatSessions.Count(), Is.EqualTo(1));
        }
    }

    /// <summary>Resolver de nome que devolve o fallback (username) — o teste não avalia o nome.</summary>
    private sealed class FallbackNameResolver : IChatUserNameResolver
    {
        public Task<string> ResolveAsync(Guid? userId, string fallback, CancellationToken cancellationToken = default)
            => Task.FromResult(string.IsNullOrWhiteSpace(fallback) ? "suporte" : fallback.Trim());
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

    private sealed class CapturingDispatcher : IAgentCommandDispatcher
    {
        public List<AgentCommand> Dispatched { get; } = [];

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            Dispatched.Add(command);
            return Task.FromResult(command);
        }
    }

    /// <summary>Timeline do chamado é efeito colateral: aqui só o estado importa.</summary>
    private sealed class NoopActivityLog : IActivityLogService
    {
        public Task<TicketActivityLog> LogStateChangeAsync(Guid ticketId, Guid? changedByUserId, Guid oldStateId, Guid newStateId)
            => Task.FromResult(new TicketActivityLog());

        public Task<TicketActivityLog> LogAssignmentAsync(Guid ticketId, Guid? changedByUserId, Guid? oldUserId, Guid? newUserId)
            => Task.FromResult(new TicketActivityLog());

        public Task<TicketActivityLog> LogActivityAsync(Guid ticketId, TicketActivityType type, Guid? changedByUserId,
            string? oldValue = null, string? newValue = null, string? comment = null)
            => Task.FromResult(new TicketActivityLog());

        public Task<TicketActivityLog> LogPriorityChangeAsync(Guid ticketId, Guid? changedByUserId, string oldPriority, string newPriority)
            => Task.FromResult(new TicketActivityLog());

        public Task<TicketActivityLog> LogDepartmentChangeAsync(Guid ticketId, Guid? changedByUserId, string oldDept, string newDept)
            => Task.FromResult(new TicketActivityLog());
    }
}

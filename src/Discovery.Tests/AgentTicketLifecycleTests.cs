using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Tickets;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Regressões do ciclo de vida de chamados pelo agent: comentário bloqueado em
/// chamado encerrado, e posse (anti-IDOR) na reabertura e na avaliação CSAT.
/// </summary>
public class AgentTicketLifecycleTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"agent-ticket-lifecycle-{Guid.NewGuid():N}")
            .Options;
        return new LifecycleTestDbContext(options);
    }

    private static WorkflowState NewState(string name, bool isFinal) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        IsInitial = !isFinal,
        IsFinal = isFinal,
        SortOrder = isFinal ? 2 : 1
    };

    private static Ticket NewTicket(Guid clientId, Guid stateId, Guid agentId, DateTime? closedAt = null)
    {
        var now = DateTime.UtcNow;
        return new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AgentId = agentId,
            Title = "Chamado do agent",
            Description = "Descrição",
            WorkflowStateId = stateId,
            Priority = TicketPriority.Medium,
            CreatedAt = now,
            UpdatedAt = now,
            ClosedAt = closedAt
        };
    }

    private static async Task<DiscoveryDbContext> SeedAsync(Ticket ticket, WorkflowState state)
    {
        var db = CreateDb();
        var client = new Client { Id = ticket.ClientId, Name = "Cliente", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        state.ClientId = null;
        db.AddRange(client, state, ticket);
        await db.SaveChangesAsync();
        return db;
    }

    [Test]
    public async Task AddComment_RejectsClosedTicket()
    {
        var agentId = Guid.NewGuid();
        var finalState = NewState("Fechado", isFinal: true);
        var ticket = NewTicket(Guid.NewGuid(), finalState.Id, agentId, closedAt: DateTime.UtcNow);
        await using var db = await SeedAsync(ticket, finalState);

        var handler = new AddMyTicketCommentHandler(
            new NoopTicketCommandService(),
            new TicketRepository(db, new NoopAgentMessaging()),
            new WorkflowRepository(db),
            NullLogger<AddMyTicketCommentHandler>.Instance);

        var result = await handler.Handle(
            new AddMyTicketCommentCommand(agentId, ticket.Id, "ainda estou com problema", false), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task AddComment_RejectsTicketInFinalStateWithoutClosedAt()
    {
        var agentId = Guid.NewGuid();
        var finalState = NewState("Resolvido", isFinal: true);
        var ticket = NewTicket(Guid.NewGuid(), finalState.Id, agentId);
        await using var db = await SeedAsync(ticket, finalState);

        var handler = new AddMyTicketCommentHandler(
            new NoopTicketCommandService(),
            new TicketRepository(db, new NoopAgentMessaging()),
            new WorkflowRepository(db),
            NullLogger<AddMyTicketCommentHandler>.Instance);

        var result = await handler.Handle(
            new AddMyTicketCommentCommand(agentId, ticket.Id, "comentario", false), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task AddComment_AllowsOpenTicket()
    {
        var agentId = Guid.NewGuid();
        var openState = NewState("Aberto", isFinal: false);
        var ticket = NewTicket(Guid.NewGuid(), openState.Id, agentId);
        await using var db = await SeedAsync(ticket, openState);

        var handler = new AddMyTicketCommentHandler(
            new NoopTicketCommandService(),
            new TicketRepository(db, new NoopAgentMessaging()),
            new WorkflowRepository(db),
            NullLogger<AddMyTicketCommentHandler>.Instance);

        var result = await handler.Handle(
            new AddMyTicketCommentCommand(agentId, ticket.Id, "comentario", false), default);

        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task Reopen_RejectsTicketFromAnotherAgent()
    {
        var ownerAgentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var state = NewState("Fechado", isFinal: true);
        var ticket = NewTicket(Guid.NewGuid(), state.Id, ownerAgentId, closedAt: DateTime.UtcNow);
        await using var db = await SeedAsync(ticket, state);

        var reopenHandler = new RecordingReopenHandler();
        var handler = new ReopenMyTicketHandler(new TicketRepository(db, new NoopAgentMessaging()), reopenHandler);

        var result = await handler.Handle(new ReopenMyTicketCommand(otherAgentId, ticket.Id, null), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
        Assert.That(reopenHandler.LastRequest, Is.Null, "handler não deve delegar quando a posse falha");
    }

    [Test]
    public async Task Reopen_DelegatesToPortalCommandWhenOwned()
    {
        var agentId = Guid.NewGuid();
        var state = NewState("Fechado", isFinal: true);
        var ticket = NewTicket(Guid.NewGuid(), state.Id, agentId, closedAt: DateTime.UtcNow);
        await using var db = await SeedAsync(ticket, state);

        var reopenHandler = new RecordingReopenHandler();
        var handler = new ReopenMyTicketHandler(new TicketRepository(db, new NoopAgentMessaging()), reopenHandler);

        var result = await handler.Handle(new ReopenMyTicketCommand(agentId, ticket.Id, "cliente pediu"), default);

        Assert.That(result.IsSuccess, Is.True);
        var delegated = reopenHandler.LastRequest;
        Assert.That(delegated, Is.Not.Null);
        Assert.That(delegated!.TicketId, Is.EqualTo(ticket.Id));
        Assert.That(delegated.Reason, Is.EqualTo("cliente pediu"));
    }

    [Test]
    public async Task Rate_RejectsTicketFromAnotherAgent()
    {
        var ownerAgentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var state = NewState("Fechado", isFinal: true);
        var ticket = NewTicket(Guid.NewGuid(), state.Id, ownerAgentId, closedAt: DateTime.UtcNow);
        await using var db = await SeedAsync(ticket, state);

        var rateHandler = new RecordingRateHandler();
        var handler = new RateMyTicketHandler(new TicketRepository(db, new NoopAgentMessaging()), rateHandler);

        var result = await handler.Handle(new RateMyTicketCommand(otherAgentId, ticket.Id, 5, "otimo", "PC-01"), default);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
        Assert.That(rateHandler.LastRequest, Is.Null);
    }

    [Test]
    public async Task Rate_DelegatesToPortalCommandWhenOwned()
    {
        var agentId = Guid.NewGuid();
        var state = NewState("Fechado", isFinal: true);
        var ticket = NewTicket(Guid.NewGuid(), state.Id, agentId, closedAt: DateTime.UtcNow);
        await using var db = await SeedAsync(ticket, state);

        var rateHandler = new RecordingRateHandler();
        var handler = new RateMyTicketHandler(new TicketRepository(db, new NoopAgentMessaging()), rateHandler);

        var result = await handler.Handle(new RateMyTicketCommand(agentId, ticket.Id, 4, "bom atendimento", "PC-01"), default);

        Assert.That(result.IsSuccess, Is.True);
        var delegated = rateHandler.LastRequest;
        Assert.That(delegated, Is.Not.Null);
        Assert.That(delegated!.TicketId, Is.EqualTo(ticket.Id));
        Assert.That(delegated.Rating, Is.EqualTo(4));
        Assert.That(delegated.Feedback, Is.EqualTo("bom atendimento"));
        Assert.That(delegated.RatedByName, Is.EqualTo("PC-01"));
    }

    // Contrato do agent: a listagem/detalhe devolvem AgentTicketDto, nunca a
    // entidade Ticket crua (campos internos não podem vazar para a máquina).
    [Test]
    public async Task GetMyTicket_ReturnsAgentContractWithoutInternalFields()
    {
        var agentId = Guid.NewGuid();
        var state = NewState("Aberto", isFinal: false);
        var ticket = NewTicket(Guid.NewGuid(), state.Id, agentId);
        ticket.AssignedToUserId = Guid.NewGuid();
        ticket.RequesterUserId = Guid.NewGuid();
        ticket.SlaExpiresAt = DateTime.UtcNow.AddHours(4);
        ticket.SlaBreached = true;
        await using var db = await SeedAsync(ticket, state);

        var handler = new GetMyTicketHandler(new TicketRepository(db, new NoopAgentMessaging()));
        var result = await handler.Handle(new GetMyTicketQuery(agentId, ticket.Id), default);

        Assert.That(result.IsSuccess, Is.True);

        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var keys = JsonDocument.Parse(json).RootElement.EnumerateObject()
            .Select(p => p.Name).ToHashSet();

        Assert.That(keys, Does.Contain("workflowStateId"));
        Assert.That(keys, Does.Contain("ratingFeedback"));
        Assert.That(keys, Does.Contain("submissionSnapshotMarkdown"));
        // departmentId e publico e ajuda a IA a chamar get_department_fields.
        Assert.That(keys, Does.Contain("departmentId"));
        Assert.That(keys, Does.Contain("templateId"));
        Assert.That(keys, Does.Contain("templateName"));

        foreach (var forbidden in new[]
                 {
                     "assignedToUserId", "requesterUserId", "deletedAt",
                     "slaExpiresAt", "slaBreached", "firstRespondedAt",
                     "firstResponseSlaStartedAt", "slaPausedSeconds", "slaHoldStartedAt",
                     "daysOpen", "workflowProfileId"
                 })
        {
            Assert.That(keys, Does.Not.Contain(forbidden),
                $"campo interno '{forbidden}' não deve vazar no contrato do agent");
        }
    }

    [Test]
    public async Task GetMyTickets_ProjectsOwnedTicketsOnly()
    {
        var agentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var state = NewState("Aberto", isFinal: false);
        var mine = NewTicket(Guid.NewGuid(), state.Id, agentId);
        var theirs = NewTicket(Guid.NewGuid(), state.Id, otherAgentId);
        await using var db = await SeedAsync(mine, state);
        db.Tickets.Add(theirs);
        await db.SaveChangesAsync();

        var handler = new GetMyTicketsHandler(new TicketRepository(db, new NoopAgentMessaging()));
        var result = await handler.Handle(new GetMyTicketsQuery(agentId, null), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Select(t => t.Id), Is.EquivalentTo(new[] { mine.Id }));
    }

    [Test]
    public async Task GetMyTicketComments_ProjectsPublicContractOnly()
    {
        var agentId = Guid.NewGuid();
        var state = NewState("Aberto", isFinal: false);
        var ticket = NewTicket(Guid.NewGuid(), state.Id, agentId);
        await using var db = await SeedAsync(ticket, state);
        db.TicketComments.AddRange(
            new TicketComment { Id = Guid.NewGuid(), TicketId = ticket.Id, Author = "Usuario", Content = "comentario publico", IsInternal = false, CreatedAt = DateTime.UtcNow },
            new TicketComment { Id = Guid.NewGuid(), TicketId = ticket.Id, Author = "Tecnico", Content = "nota interna", IsInternal = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var handler = new GetMyTicketCommentsHandler(new TicketRepository(db, new NoopAgentMessaging()));
        var result = await handler.Handle(new GetMyTicketCommentsQuery(agentId, ticket.Id), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!, Has.Count.EqualTo(1), "nota interna nao pode aparecer para o agent");
        Assert.That(result.Value![0].Content, Is.EqualTo("comentario publico"));

        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement[0].EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.That(keys, Does.Not.Contain("isInternal"));
        Assert.That(keys, Does.Not.Contain("ticketId"));
    }

    [Test]
    public async Task GetMyTicketAnswers_ReturnsOnlyOwnedTicketAnswers()
    {
        var agentId = Guid.NewGuid();
        var state = NewState("Aberto", isFinal: false);
        var ticket = NewTicket(Guid.NewGuid(), state.Id, agentId);
        await using var db = await SeedAsync(ticket, state);
        db.TicketAnswers.Add(new TicketAnswer
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            QuestionKey = "tipo",
            QuestionLabel = "Tipo de equipamento",
            ValueText = "Notebook",
            ValueJson = "\"Notebook\"",
            SortOrder = 0,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var handler = new GetMyTicketAnswersHandler(new TicketRepository(db, new NoopAgentMessaging()), db);
        var result = await handler.Handle(new GetMyTicketAnswersQuery(agentId, ticket.Id), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Select(a => a.QuestionLabel), Is.EquivalentTo(new[] { "Tipo de equipamento" }));
        Assert.That(result.Value![0].ValueText, Is.EqualTo("Notebook"));

        // Isolamento por agent (IDOR)
        var other = await handler.Handle(new GetMyTicketAnswersQuery(Guid.NewGuid(), ticket.Id), default);
        Assert.That(other.IsFailure, Is.True);
    }

    // Guarda de injeção: os handlers do agent dependem da interface fechada dos
    // handlers do portal. Se o MediatR deixar de registrar essas interfaces, as
    // rotas /reopen e /rating quebram em runtime (a transação do comando externo
    // precisa continuar sendo a única do fluxo).
    [Test]
    public void MediatRRegistersPortalLifecycleHandlersForDirectDelegation()
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ReopenTicketCommandHandler).Assembly));

        Assert.That(
            services.Any(d => d.ServiceType == typeof(IRequestHandler<ReopenTicketCommand, Result<TicketDetailDto>>)),
            Is.True, "ReopenTicketCommandHandler não registrado como IRequestHandler<ReopenTicketCommand, ...>");
        Assert.That(
            services.Any(d => d.ServiceType == typeof(IRequestHandler<RateTicketCommand, Result<TicketDetailDto>>)),
            Is.True, "RateTicketCommandHandler não registrado como IRequestHandler<RateTicketCommand, ...>");
    }

    private static TicketDetailDto SampleDto() => new(
        Id: Guid.NewGuid(),
        ClientId: Guid.NewGuid(),
        SiteId: null,
        AgentId: null,
        Title: "Chamado",
        Description: "Descrição",
        Category: null,
        Priority: TicketPriority.Medium,
        WorkflowStateId: Guid.NewGuid(),
        AssignedToUserId: null,
        SlaExpiresAt: null,
        SlaBreached: false,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        ClosedAt: null,
        DaysOpen: 0);

    private sealed class RecordingReopenHandler : IRequestHandler<ReopenTicketCommand, Result<TicketDetailDto>>
    {
        public ReopenTicketCommand? LastRequest { get; private set; }
        public Result<TicketDetailDto> Response { get; set; } = Result<TicketDetailDto>.Success(SampleDto());

        public Task<Result<TicketDetailDto>> Handle(ReopenTicketCommand request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private sealed class RecordingRateHandler : IRequestHandler<RateTicketCommand, Result<TicketDetailDto>>
    {
        public RateTicketCommand? LastRequest { get; private set; }
        public Result<TicketDetailDto> Response { get; set; } = Result<TicketDetailDto>.Success(SampleDto());

        public Task<Result<TicketDetailDto>> Handle(RateTicketCommand request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    private sealed class NoopTicketCommandService : ITicketCommandService
    {
        public Task<Ticket> CreateTicketAsync(string title, string description, TicketPriority priority, Guid clientId, Guid? siteId, Guid? agentId, Guid? departmentId, Guid? workflowProfileId, Guid? assignedToUserId, string? category, CancellationToken ct = default, string? submissionSnapshotMarkdown = null, Guid? templateId = null, string? templateName = null, Guid? requesterUserId = null)
            => throw new NotSupportedException();

        public Task<Ticket> UpdateTicketAsync(Guid ticketId, string? title, string? description, TicketPriority? priority, Guid? departmentId, Guid? workflowProfileId, Guid? assignedToUserId, string? category, bool clearDepartment = false, bool clearWorkflowProfile = false, CancellationToken ct = default, Guid? requesterUserId = null, bool clearRequester = false, Guid? agentId = null, bool clearAgent = false, Guid? changedByUserId = null)
            => throw new NotSupportedException();

        public Task<TicketComment> AddCommentAsync(Guid ticketId, string content, bool isInternal, Guid? userId, string? userName, CancellationToken ct = default)
            => Task.FromResult(new TicketComment { Id = Guid.NewGuid(), TicketId = ticketId, Content = content, IsInternal = isInternal });

        public Task<Ticket> AssignTicketAsync(Guid ticketId, Guid? assignedToUserId, Guid? changedByUserId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> BackfillDepartmentProfileAsync(WorkflowProfile profile, Guid? changedByUserId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class LifecycleTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client), typeof(WorkflowState), typeof(Ticket), typeof(TicketComment),
                typeof(TicketActivityLog), typeof(TicketAnswer)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<WorkflowState>(e => e.HasKey(s => s.Id));
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<TicketComment>(e => e.HasKey(c => c.Id));
            modelBuilder.Entity<TicketActivityLog>(e => e.HasKey(l => l.Id));
            modelBuilder.Entity<TicketAnswer>(e => { e.HasKey(a => a.Id); e.Ignore(a => a.Embedding); });
        }
    }

    private sealed class NoopAgentMessaging : IAgentMessaging
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

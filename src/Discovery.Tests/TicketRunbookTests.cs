using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Tickets;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Runbooks executáveis no ticket (M242): ordem de execução, falha de um passo
/// não aborta os seguintes e evidência anexada como comentário.
/// </summary>
[TestFixture]
public class TicketRunbookTests
{
    [Test]
    public async Task ExecutesSteps_InOrder_AndAttachesEvidence()
    {
        var macroId = Guid.NewGuid();
        var script1 = Guid.NewGuid();
        var script2 = Guid.NewGuid();
        await using var db = NewDb();
        Seed(db, macroId, script1, script2);
        var dispatcher = new FakeDispatcher();
        var scriptService = new FakeScriptService(script1, script2);
        var reportRepo = new FakeReportRepo(okOnFirst: true);
        var mediator = new CommentMediator();
        var handler = BuildHandler(db, dispatcher, scriptService, reportRepo, mediator);

        var result = await handler.Handle(new ExecuteTicketRunbookCommand(
            db.Tickets.First().Id, macroId, Guid.NewGuid(), "op"), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(dispatcher.DispatchOrder, Is.EqualTo(new[] { "script-1", "script-2" }),
                "passos executam na ordem configurada (Order 1 antes de Order 2)");
            Assert.That(result.Value.Steps.All(s => s.Ok), Is.True);
            Assert.That(result.Value.Steps.All(s => s.ExitCode == 0), Is.True);
            Assert.That(mediator.Comments.Count, Is.EqualTo(2), "um comentário de evidência por passo");
            Assert.That(mediator.Comments[0], Does.Contain("Evidência do passo 1"));
            Assert.That(result.Value.Steps.All(s => s.CommentId is not null), Is.True);
        });
    }

    [Test]
    public async Task StepFailure_DoesNotAbortFollowingSteps()
    {
        var macroId = Guid.NewGuid();
        var script1 = Guid.NewGuid();
        var script2 = Guid.NewGuid();
        await using var db = NewDb();
        Seed(db, macroId, script1, script2);
        var scriptService = new FakeScriptService(script1, script2);
        var reportRepo = new FakeReportRepo(okOnFirst: false); // passo 1 falha
        var mediator = new CommentMediator();
        var handler = BuildHandler(db, new FakeDispatcher(), scriptService, reportRepo, mediator);

        var result = await handler.Handle(new ExecuteTicketRunbookCommand(
            db.Tickets.First().Id, macroId, Guid.NewGuid(), "op"), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Value.Steps.Count, Is.EqualTo(2), "passo 2 continua após a falha do passo 1");
            Assert.That(result.Value.Steps[0].Ok, Is.False, "passo 1 marcado ok=false");
            Assert.That(result.Value.Steps[1].Ok, Is.True);
            Assert.That(mediator.Comments.Count, Is.EqualTo(2));
            Assert.That(mediator.Comments[0], Does.Contain("FALHA"));
        });
    }

    [Test]
    public async Task MissingScript_MarksStepFailed_AndContinues()
    {
        var macroId = Guid.NewGuid();
        var script1 = Guid.NewGuid();
        var script2 = Guid.NewGuid();
        await using var db = NewDb();
        Seed(db, macroId, script1, script2);
        var mediator = new CommentMediator();
        // FakeScriptService só conhece script2 — script1 "não existe".
        var handler = BuildHandler(db, new FakeDispatcher(), new FakeScriptService(script2), new FakeReportRepo(true), mediator);

        var result = await handler.Handle(new ExecuteTicketRunbookCommand(
            db.Tickets.First().Id, macroId, Guid.NewGuid(), "op"), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Value.Steps[0].Ok, Is.False);
            Assert.That(result.Value.Steps[0].OutputPreview, Does.Contain("não encontrado"));
            Assert.That(result.Value.Steps[1].Ok, Is.True);
        });
    }

    [Test]
    public async Task TicketWithoutAgent_ReturnsValidationError()
    {
        var macroId = Guid.NewGuid();
        var script1 = Guid.NewGuid();
        await using var db = NewDb();
        Seed(db, macroId, script1, script1, withAgent: false);
        var handler = BuildHandler(db, new FakeDispatcher(), new FakeScriptService(script1), new FakeReportRepo(true), new CommentMediator());

        var result = await handler.Handle(new ExecuteTicketRunbookCommand(
            db.Tickets.First().Id, macroId, Guid.NewGuid(), "op"), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        });
    }

    [Test]
    public async Task MacroWithoutRunbook_ReturnsValidationError()
    {
        var macroId = Guid.NewGuid();
        var script1 = Guid.NewGuid();
        await using var db = NewDb();
        Seed(db, macroId, script1, script1, runbookJson: "");
        var handler = BuildHandler(db, new FakeDispatcher(), new FakeScriptService(script1), new FakeReportRepo(true), new CommentMediator());

        var result = await handler.Handle(new ExecuteTicketRunbookCommand(
            db.Tickets.First().Id, macroId, Guid.NewGuid(), "op"), default);

        Assert.That(result.IsFailure, Is.True);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private static ExecuteTicketRunbookCommandHandler BuildHandler(
        DiscoveryDbContext db, IAgentCommandDispatcher dispatcher,
        IAutomationScriptService scriptService, IAutomationExecutionReportRepository reportRepo,
        IMediator mediator)
        => new(db, scriptService, dispatcher, reportRepo, mediator, NullLogger<ExecuteTicketRunbookCommandHandler>.Instance);


    [Test]
    public async Task StopOnFailure_SkipsRemainingSteps()
    {
        var macroId = Guid.NewGuid();
        var script1 = Guid.NewGuid();
        var script2 = Guid.NewGuid();
        var script3 = Guid.NewGuid();
        await using var db = NewDb();
        Seed(db, macroId, script1, script2, runbookJson: Discovery.Core.DTOs.RunbookStepList.ToJson(new List<RunbookStep>
        {
            new(1, script1, StopOnFailure: true),
            new(2, script2),
            new(3, script3)
        }));
        var dispatcher = new FakeDispatcher();
        var scriptService = new FakeScriptService(script1, script2, script3);
        // passo 1 falha (report com erro)
        var reportRepo = new FakeReportRepo(okOnFirst: false);
        var mediator = new CommentMediator();
        var handler = BuildHandler(db, dispatcher, scriptService, reportRepo, mediator);

        var result = await handler.Handle(new ExecuteTicketRunbookCommand(
            db.Tickets.First().Id, macroId, Guid.NewGuid(), "op"), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            // passo 1 executou e falhou; passo 2 pulado; passo 3 pulado
            Assert.That(result.Value.Steps.Single(s => s.Order == 1).Ok, Is.False);
            Assert.That(result.Value.Steps.Single(s => s.Order == 2).Ok, Is.False);
            Assert.That(result.Value.Steps.Single(s => s.Order == 2).OutputPreview, Does.Contain("Passo pulado"));
            Assert.That(result.Value.Steps.Single(s => s.Order == 3).OutputPreview, Does.Contain("Passo pulado"));
            Assert.That(dispatcher.DispatchOrder, Is.EqualTo(new[] { "script-1" }),
                "com StopOnFailure, passos seguintes não são despachados");
        });
    }

    private static void Seed(DiscoveryDbContext db, Guid macroId, Guid script1, Guid script2, bool withAgent = true, string? runbookJson = null)
    {
        runbookJson ??= Discovery.Core.DTOs.RunbookStepList.ToJson(new List<RunbookStep>
        {
            new(2, script2, EvidenceLabel: "Evidência do passo 2"),
            new(1, script1, EvidenceLabel: "Evidência do passo 1")
        });

        db.TicketMacros.Add(new TicketMacro
        {
            Id = macroId, Name = "Runbook de limpeza", Content = "conteúdo",
            IsActive = true, RunbookStepsJson = runbookJson, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AgentId = withAgent ? Guid.NewGuid() : null,
            Title = "ticket runbook",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private sealed class FakeDispatcher : IAgentCommandDispatcher
    {
        public List<string> DispatchOrder { get; } = [];
        private int _seq;

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            _seq++;
            command.Id = Guid.NewGuid();
            DispatchOrder.Add($"script-{_seq}");
            return Task.FromResult(command);
        }
    }

    private sealed class FakeScriptService(params Guid[] knownIds) : IAutomationScriptService
    {
        public Task<AutomationScriptDetailDto?> GetByIdAsync(Guid id, bool includeInactive = false, CancellationToken cancellationToken = default)
        {
            if (!knownIds.Contains(id))
                return Task.FromResult<AutomationScriptDetailDto?>(null);

            return Task.FromResult<AutomationScriptDetailDto?>(new AutomationScriptDetailDto
            {
                Id = id,
                Name = "Script " + id.ToString("N")[..6],
                Content = $"echo script-{id}",
                ContentHashSha256 = "hash"
            });
        }

        public Task<CursorPageDto<AutomationScriptSummaryDto>> GetListPageAsync(Guid? clientId, bool activeOnly, string? cursor, int limit, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AutomationScriptDetailDto> CreateAsync(CreateAutomationScriptRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AutomationScriptDetailDto?> UpdateAsync(Guid id, UpdateAutomationScriptRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, string? reason, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AutomationScriptAuditDto>> GetAuditAsync(Guid id, int limit = 100, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AutomationScriptConsumeDto?> GetConsumePayloadAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeReportRepo(bool okOnFirst) : IAutomationExecutionReportRepository
    {
        private int _calls;

        public Task<AutomationExecutionReport> CreateAsync(AutomationExecutionReport report)
        {
            report.Id = Guid.NewGuid();
            return Task.FromResult(report);
        }

        public Task<AutomationExecutionReport?> GetByCommandIdAsync(Guid commandId)
        {
            _calls++;
            var ok = okOnFirst || _calls > 1;
            return Task.FromResult<AutomationExecutionReport?>(new AutomationExecutionReport
            {
                Id = Guid.NewGuid(),
                CommandId = commandId,
                AgentId = Guid.NewGuid(),
                ScriptId = Guid.NewGuid(),
                Status = ok ? AutomationExecutionStatus.Completed : AutomationExecutionStatus.Failed,
                ExitCode = ok ? 0 : 2,
                ErrorMessage = ok ? null : "exit code 2",
                ResultMetadataJson = ok ? "saida do script" : null
            });
        }

        public Task<AutomationExecutionReport?> GetByIdAsync(Guid id) => Task.FromResult<AutomationExecutionReport?>(null);
        public Task<bool> MarkCancelledAsync(Guid executionId, string? errorMessage, DateTime cancelledAt) => Task.FromResult(true);
        public Task<IReadOnlyList<AutomationExecutionReport>> GetByAgentIdAsync(Guid agentId, int limit = 100, AutomationExecutionStatus? status = null, AutomationExecutionSourceType? sourceType = null, Guid? taskId = null, Guid? scriptId = null, string? correlationId = null)
            => Task.FromResult<IReadOnlyList<AutomationExecutionReport>>([]);
        public Task<IReadOnlyList<AutomationExecutionReport>> GetByTaskIdAsync(Guid taskId, int limit = 100)
            => Task.FromResult<IReadOnlyList<AutomationExecutionReport>>([]);
        public Task UpdateAckAsync(Guid commandId, Guid? taskId, Guid? scriptId, string? ackMetadataJson, DateTime acknowledgedAt, string? correlationId) => Task.CompletedTask;
        public Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId) => Task.CompletedTask;
        public Task UpdateResultFromCommandAsync(Guid commandId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt) => Task.CompletedTask;
        public Task UpsertPolicyExecutionAsync(Guid agentId, Guid commandId, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, AutomationExecutionStatus status, string? correlationId) => Task.CompletedTask;
    }

    private sealed class CommentMediator : IMediator
    {
        public List<string> Comments { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is AddTicketCommentCommand cmd)
            {
                Comments.Add(cmd.Content);
                var payload = new AddTicketCommentResult(Guid.NewGuid(), DateTime.UtcNow);
                var wrapped = Result<AddTicketCommentResult>.Success(payload);
                return Task.FromResult((TResponse)(object)wrapped);
            }

            throw new NotSupportedException(request?.GetType().Name);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
            => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    // ── DbContext restrito ──────────────────────────────────────────────────

    private static RunbookTestDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"ticket-runbook-{Guid.NewGuid():N}")
            .Options;
        return new RunbookTestDbContext(options);
    }

    /// <summary>Contexto restrito a Ticket/TicketMacro (InMemory não suporta o modelo completo).</summary>
    private sealed class RunbookTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var included = new[] { typeof(Ticket), typeof(TicketMacro) };
            foreach (var entityType in typeof(Discovery.Core.Entities.Agent).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !included.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Ticket>(entity => entity.HasKey(x => x.Id));
            modelBuilder.Entity<TicketMacro>(entity => entity.HasKey(x => x.Id));
        }
    }
}
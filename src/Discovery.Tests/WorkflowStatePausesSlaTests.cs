using Discovery.Core.Cqrs.WorkflowState.Commands;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Cqrs.WorkflowState;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Cobre o flag "desconsiderar SLA" (workflow_states.pauses_sla) no caminho de
/// edição: o command handler aceitava o campo e o DTO o devolvia, mas o
/// repositório não copiava PausesSla para a entidade rastreada — a alteração
/// era silenciosamente descartada no SaveChanges.
/// </summary>
[TestFixture]
public class WorkflowStatePausesSlaTests
{
    private static DbContextOptions<DiscoveryDbContext> Options(string name) =>
        new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase(name)
            .Options;

    private static DiscoveryDbContext NewDb(string name) => new WorkflowStateTestDbContext(Options(name));

    private static WorkflowState NewState() => new()
    {
        Id = Guid.NewGuid(),
        ClientId = null,
        Name = "Aguardando cliente",
        Color = "#8b5cf6",
        IsInitial = false,
        IsFinal = false,
        SortOrder = 3,
        PausesSla = false,
        CreatedAt = DateTime.UtcNow,
    };

    [Test]
    public async Task UpdateStateAsync_PersistsPausesSla()
    {
        var dbName = $"workflow-pauses-sla-{Guid.NewGuid():N}";
        var state = NewState();

        await using (var seedDb = NewDb(dbName))
        {
            seedDb.WorkflowStates.Add(state);
            await seedDb.SaveChangesAsync();
        }

        await using (var db = NewDb(dbName))
        {
            var repo = new WorkflowRepository(db);
            var loaded = await repo.GetStateByIdAsync(state.Id);
            Assert.That(loaded, Is.Not.Null);
            loaded!.PausesSla = true;
            await repo.UpdateStateAsync(loaded);
        }

        await using (var verifyDb = NewDb(dbName))
        {
            var persisted = await verifyDb.WorkflowStates.AsNoTracking().SingleAsync(s => s.Id == state.Id);
            Assert.That(persisted.PausesSla, Is.True, "PausesSla não sobreviveu ao UpdateStateAsync.");
        }
    }

    [Test]
    public async Task UpdateCommand_ToggleOnAndOff_RoundTripsThroughHandler()
    {
        await using var db = NewDb($"workflow-pauses-sla-cmd-{Guid.NewGuid():N}");
        var state = NewState();
        db.WorkflowStates.Add(state);
        await db.SaveChangesAsync();

        var handler = new UpdateWorkflowStateCommandHandler(new WorkflowRepository(db));

        var on = await handler.Handle(new UpdateWorkflowStateCommand(state.Id, null, null, null, null, null, true), CancellationToken.None);
        Assert.That(on.IsSuccess, Is.True);
        Assert.That(on.Value!.PausesSla, Is.True);
        Assert.That((await new WorkflowRepository(db).GetStateByIdAsync(state.Id))!.PausesSla, Is.True);

        var off = await handler.Handle(new UpdateWorkflowStateCommand(state.Id, null, null, null, null, null, false), CancellationToken.None);
        Assert.That(off.IsSuccess, Is.True);
        Assert.That(off.Value!.PausesSla, Is.False);
        Assert.That((await new WorkflowRepository(db).GetStateByIdAsync(state.Id))!.PausesSla, Is.False);
    }

    // ── Validações de configuração (B3/B4/B5) ────────────────────────────

    private static DiscoveryDbContext NewConfigDb(string name) =>
        new WorkflowConfigTestDbContext(
            new DbContextOptionsBuilder<DiscoveryDbContext>().UseInMemoryDatabase(name).Options);

    [Test]
    public async Task CreateCommand_RejectsEmptyName()
    {
        await using var db = NewConfigDb($"wf-create-name-{Guid.NewGuid():N}");
        var handler = new CreateWorkflowStateCommandHandler(new WorkflowRepository(db));

        var result = await handler.Handle(
            new CreateWorkflowStateCommand(null, "   ", null, false, false, 0, false), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("Name"));
    }

    [Test]
    public async Task CreateCommand_RejectsInitialAndFinalAtOnce()
    {
        await using var db = NewConfigDb($"wf-create-if-{Guid.NewGuid():N}");
        var handler = new CreateWorkflowStateCommandHandler(new WorkflowRepository(db));

        var result = await handler.Handle(
            new CreateWorkflowStateCommand(null, "Estado", null, true, true, 0, false), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task CreateCommand_RejectsNegativeSortOrder()
    {
        await using var db = NewConfigDb($"wf-create-order-{Guid.NewGuid():N}");
        var handler = new CreateWorkflowStateCommandHandler(new WorkflowRepository(db));

        var result = await handler.Handle(
            new CreateWorkflowStateCommand(null, "Estado", null, false, false, -1, false), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("SortOrder"));
    }

    [Test]
    public async Task CreateCommand_RejectsSecondInitialInSameScope()
    {
        await using var db = NewConfigDb($"wf-create-dup-init-{Guid.NewGuid():N}");
        var clientId = Guid.NewGuid();
        db.WorkflowStates.Add(new WorkflowState
        {
            Id = Guid.NewGuid(), ClientId = clientId, Name = "Inicial",
            IsInitial = true, SortOrder = 1, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var handler = new CreateWorkflowStateCommandHandler(new WorkflowRepository(db));
        var result = await handler.Handle(
            new CreateWorkflowStateCommand(clientId, "Outro inicial", null, true, false, 2, false), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("IsInitial"));
    }

    [Test]
    public async Task UpdateCommand_ClearsColor_WhenEmpty()
    {
        await using var db = NewConfigDb($"wf-update-color-{Guid.NewGuid():N}");
        var state = NewState(); // Color = #8b5cf6
        db.WorkflowStates.Add(state);
        await db.SaveChangesAsync();

        var handler = new UpdateWorkflowStateCommandHandler(new WorkflowRepository(db));
        var result = await handler.Handle(
            new UpdateWorkflowStateCommand(state.Id, null, "", null, null, null, null), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Color, Is.Null);
        Assert.That((await new WorkflowRepository(db).GetStateByIdAsync(state.Id))!.Color, Is.Null,
            "cor vazia deve limpar a cor persistida (semântica PUT).");
    }

    [Test]
    public async Task UpdateCommand_RejectsEmptyName()
    {
        await using var db = NewConfigDb($"wf-update-name-{Guid.NewGuid():N}");
        var state = NewState();
        db.WorkflowStates.Add(state);
        await db.SaveChangesAsync();

        var handler = new UpdateWorkflowStateCommandHandler(new WorkflowRepository(db));
        var result = await handler.Handle(
            new UpdateWorkflowStateCommand(state.Id, "   ", null, null, null, null, null), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("Name"));
    }

    [Test]
    public async Task CreateTransition_RejectsSameState()
    {
        await using var db = NewConfigDb($"wf-tr-same-{Guid.NewGuid():N}");
        var state = NewState();
        db.WorkflowStates.Add(state);
        await db.SaveChangesAsync();

        var handler = new CreateWorkflowTransitionCommandHandler(new WorkflowRepository(db));
        var result = await handler.Handle(
            new CreateWorkflowTransitionCommand(null, state.Id, state.Id, "loop"), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("ToStateId"));
    }

    [Test]
    public async Task CreateTransition_RejectsMissingStates()
    {
        await using var db = NewConfigDb($"wf-tr-missing-{Guid.NewGuid():N}");
        var state = NewState();
        db.WorkflowStates.Add(state);
        await db.SaveChangesAsync();

        var handler = new CreateWorkflowTransitionCommandHandler(new WorkflowRepository(db));
        var result = await handler.Handle(
            new CreateWorkflowTransitionCommand(null, state.Id, Guid.NewGuid(), "x"), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Field, Is.EqualTo("ToStateId"));
    }

    [Test]
    public async Task CreateTransition_RejectsDuplicatePair()
    {
        await using var db = NewConfigDb($"wf-tr-dup-{Guid.NewGuid():N}");
        var from = NewState();
        var to = NewState();
        to.Id = Guid.NewGuid();
        to.Name = "Final";
        to.IsFinal = true;
        db.WorkflowStates.AddRange(from, to);
        await db.SaveChangesAsync();

        var handler = new CreateWorkflowTransitionCommandHandler(new WorkflowRepository(db));
        var first = await handler.Handle(
            new CreateWorkflowTransitionCommand(null, from.Id, to.Id, "ok"), CancellationToken.None);
        Assert.That(first.IsSuccess, Is.True);

        var duplicate = await handler.Handle(
            new CreateWorkflowTransitionCommand(null, from.Id, to.Id, "dup"), CancellationToken.None);
        Assert.That(duplicate.IsFailure, Is.True);
        Assert.That(duplicate.Errors[0].Code, Is.EqualTo("Validation"));
    }

    [Test]
    public async Task DeleteState_BlockedWhenTicketsInState()
    {
        await using var db = NewConfigDb($"wf-del-block-{Guid.NewGuid():N}");
        var state = NewState();
        db.WorkflowStates.Add(state);
        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), Title = "chamado", Description = "d",
            WorkflowStateId = state.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var handler = new DeleteWorkflowStateCommandHandler(new WorkflowRepository(db));
        var result = await handler.Handle(new DeleteWorkflowStateCommand(state.Id), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Message, Does.Contain("chamados"));
        Assert.That(await db.WorkflowStates.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteStateAsync_CascadesTransitionsAndAlertRules()
    {
        await using var db = NewConfigDb($"wf-del-cascade-{Guid.NewGuid():N}");
        var a = NewState();
        var b = NewState();
        b.Id = Guid.NewGuid();
        b.Name = "Final";
        b.IsFinal = true;
        db.WorkflowStates.AddRange(a, b);
        db.WorkflowTransitions.AddRange(
            new WorkflowTransition { Id = Guid.NewGuid(), FromStateId = a.Id, ToStateId = b.Id, Name = "ab", CreatedAt = DateTime.UtcNow },
            new WorkflowTransition { Id = Guid.NewGuid(), FromStateId = b.Id, ToStateId = a.Id, Name = "ba", CreatedAt = DateTime.UtcNow },
            new WorkflowTransition { Id = Guid.NewGuid(), FromStateId = b.Id, ToStateId = b.Id, Name = "bb", CreatedAt = DateTime.UtcNow });
        db.TicketAlertRules.AddRange(
            new TicketAlertRule { Id = Guid.NewGuid(), WorkflowStateId = a.Id, Title = "A", Message = "a", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new TicketAlertRule { Id = Guid.NewGuid(), WorkflowStateId = b.Id, Title = "B", Message = "b", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await new WorkflowRepository(db).DeleteStateAsync(a.Id);

        Assert.That(await db.WorkflowStates.CountAsync(), Is.EqualTo(1));
        Assert.That(await db.WorkflowTransitions.CountAsync(), Is.EqualTo(1), "só b→b deve restar");
        Assert.That(await db.TicketAlertRules.CountAsync(), Is.EqualTo(1), "só a regra de b deve restar");
    }

    /// <summary>Contexto restrito a WorkflowState: o DiscoveryDbContext completo
    /// mapeia entidades cujo modelo o provider InMemory não suporta.</summary>
    private sealed class WorkflowStateTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(WorkflowState).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(WorkflowState)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<WorkflowState>(entity =>
            {
                entity.HasKey(state => state.Id);
                entity.Property(state => state.PausesSla);
            });
        }
    }

    /// <summary>Contexto restrito para os testes de validação/exclusão (Ticket incluso).</summary>
    private sealed class WorkflowConfigTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(WorkflowState), typeof(WorkflowTransition), typeof(TicketAlertRule), typeof(Ticket)
            };

            foreach (var entityType in typeof(WorkflowState).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<WorkflowState>(entity =>
            {
                entity.HasKey(state => state.Id);
                entity.Property(state => state.PausesSla);
            });
            modelBuilder.Entity<WorkflowTransition>(entity => entity.HasKey(transition => transition.Id));
            modelBuilder.Entity<TicketAlertRule>(entity => entity.HasKey(rule => rule.Id));
            modelBuilder.Entity<Ticket>(entity =>
            {
                entity.HasKey(ticket => ticket.Id);
                entity.Ignore(ticket => ticket.DaysOpen);
            });
        }
    }
}

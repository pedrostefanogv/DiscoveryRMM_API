using Discovery.Core.Cqrs;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Cqrs.Behaviors;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class TransactionBehaviorTests
{
    private SqliteConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    private ServiceProvider BuildServices(bool useRetryingStrategy = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RetryTestState>();
        services.AddScoped<DiscoveryDbContext>(_ =>
        {
            var builder = new DbContextOptionsBuilder<DiscoveryDbContext>()
                .UseSqlite(_connection);
            if (useRetryingStrategy)
            {
                builder.ReplaceService<IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>();
            }
            var db = new TransactionTestDbContext(builder.Options);
            db.Database.EnsureCreated();
            return db;
        });
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<TransactionBehaviorTests>();
            cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
        });
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Handle_WhenResultIsSuccess_SavesChangesAndCommitsTransaction()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var clientId = Guid.NewGuid();
        var result = await mediator.Send(new AddClientWithoutExplicitSaveCommand(clientId, "Committed"));

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(db.Database.CurrentTransaction, Is.Null);

        db.ChangeTracker.Clear();
        var persisted = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId);
        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.Name, Is.EqualTo("Committed"));
    }

    [Test]
    public async Task Handle_WhenResultGenericIsFailure_DoesNotSaveAndRollsBackIntermediateWrites()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var savedId = Guid.NewGuid();
        var trackedId = Guid.NewGuid();
        var voidResult = await mediator.Send(new IntermediateSaveThenFailVoidCommand(savedId, trackedId));

        Assert.That(voidResult.IsFailure, Is.True);
        Assert.That(db.Database.CurrentTransaction, Is.Null);
        Assert.That(db.ChangeTracker.Entries(), Is.Empty);

        var strId1 = Guid.NewGuid();
        var strId2 = Guid.NewGuid();
        var strResult = await mediator.Send(new IntermediateSaveThenFailStringCommand(strId1, strId2));
        Assert.That(strResult.IsFailure, Is.True);
        Assert.That(db.Database.CurrentTransaction, Is.Null);
        Assert.That(db.ChangeTracker.Entries(), Is.Empty);

        db.ChangeTracker.Clear();
        var count = await db.Clients.AsNoTracking()
            .CountAsync(c => c.Id == savedId || c.Id == trackedId || c.Id == strId1 || c.Id == strId2);
        Assert.That(count, Is.EqualTo(0),
            "Intermediate SaveChangesAsync and tracked entities must be rolled back on Result.Failure.");
    }

    [Test]
    public async Task Handle_WhenHandlerThrows_RollsBackAndClearsTrackerAndRethrows()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var clientId = Guid.NewGuid();
        Assert.That(
            async () => await mediator.Send(new IntermediateSaveThenThrowCommand(clientId)),
            Throws.InvalidOperationException);

        Assert.That(db.Database.CurrentTransaction, Is.Null);
        Assert.That(db.ChangeTracker.Entries(), Is.Empty);

        var persisted = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId);
        Assert.That(persisted, Is.Null);
    }

    [Test]
    public async Task Handle_WhenNestedCommandSentViaMediator_ReusesCurrentTransactionAndCommitsAtomically()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var outerId = Guid.NewGuid();
        var innerId = Guid.NewGuid();
        var result = await mediator.Send(new OuterSuccessWithNestedCommand(outerId, innerId));

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.InnerTransactionId, Is.EqualTo(result.Value.OuterTransactionId));
        Assert.That(db.Database.CurrentTransaction, Is.Null);

        db.ChangeTracker.Clear();
        var rows = await db.Clients.AsNoTracking()
            .Where(c => c.Id == outerId || c.Id == innerId)
            .ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Handle_WhenNestedCommandOrOuterCommandFails_RollsBackEntireAtomicUnit()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var outerId = Guid.NewGuid();
        var innerSuccessId = Guid.NewGuid();
        var innerFailId = Guid.NewGuid();

        var result = await mediator.Send(new OuterBulkWithFailingInnerCommand(outerId, innerSuccessId, innerFailId));

        Assert.That(result.IsFailure, Is.True);
        Assert.That(db.Database.CurrentTransaction, Is.Null);
        Assert.That(db.ChangeTracker.Entries(), Is.Empty);

        db.ChangeTracker.Clear();
        var count = await db.Clients.AsNoTracking()
            .CountAsync(c => c.Id == outerId || c.Id == innerSuccessId || c.Id == innerFailId);
        Assert.That(count, Is.EqualTo(0),
            "Outer failure after a successful nested command must roll back the entire atomic transaction.");
    }

    [Test]
    public async Task Handle_WhenOuterSwallowsInnerFailure_RollsBackInnerWritesAndCommitsOuter()
    {
        await using var sp = BuildServices();
        await using var scope = sp.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var seedId = Guid.NewGuid();
        db.Clients.Add(new Client { Id = seedId, Name = "SeedBefore", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var outerId = Guid.NewGuid();
        var innerId = Guid.NewGuid();

        var result = await mediator.Send(new OuterSwallowsInnerFailureCommand(seedId, outerId, innerId));

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(db.Database.CurrentTransaction, Is.Null);
        Assert.That(db.ChangeTracker.HasChanges(), Is.False,
            "ChangeTracker must have no pending dirty entries after outer completes.");
        Assert.That(db.ChangeTracker.Entries<Client>().Any(e => e.Entity.Id == innerId), Is.False,
            "Failed inner entity must be detached from ChangeTracker.");

        db.ChangeTracker.Clear();
        var seedRow = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == seedId);
        var outerRow = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == outerId);
        var innerRow = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == innerId);

        Assert.That(seedRow, Is.Not.Null);
        Assert.That(seedRow!.Name, Is.EqualTo("SeedBefore"),
            "Inner ExecuteUpdateAsync must be rolled back by the savepoint.");
        Assert.That(outerRow, Is.Not.Null,
            "Outer command entity must still be committed.");
        Assert.That(outerRow!.Name, Is.EqualTo("OuterCommitted"));
        Assert.That(innerRow, Is.Null,
            "Failed inner command SaveChangesAsync row must NOT be committed when outer swallows failure.");
    }

    [Test]
    public async Task Handle_WhenExecutionStrategyRetriesAfterBrokenTransactionTransientFailure_RetriesAndClearsTracker()
    {
        await using var sp = BuildServices(useRetryingStrategy: true);
        await using var scope = sp.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var retryState = scope.ServiceProvider.GetRequiredService<RetryTestState>();

        var attempt1Id = Guid.NewGuid();
        var attempt2Id = Guid.NewGuid();

        var result = await mediator.Send(new BrokenTxTransientFailThenSucceedCommand(attempt1Id, attempt2Id));

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.EqualTo(attempt2Id));
        Assert.That(retryState.Attempts, Is.EqualTo(2),
            "ExecutionStrategy must retry cleanly without being masked by RollbackAsync InvalidOperationException.");
        Assert.That(retryState.TrackerCountsAtAttemptStart, Is.EqualTo(new[] { 0, 0 }),
            "ChangeTracker must be cleared between failed and retried attempts.");
        Assert.That(db.Database.CurrentTransaction, Is.Null);

        db.ChangeTracker.Clear();
        var attempt1Row = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == attempt1Id);
        var attempt2Row = await db.Clients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == attempt2Id);
        Assert.That(attempt1Row, Is.Null, "Attempt 1 entity must NOT be persisted.");
        Assert.That(attempt2Row, Is.Not.Null, "Attempt 2 entity must be persisted.");
        Assert.That(attempt2Row!.Name, Is.EqualTo("Attempt2Committed"));
    }

    // ── Test Commands & Handlers ──────────────────────────────────────────────

    private sealed record AddClientWithoutExplicitSaveCommand(Guid Id, string Name) : ICommand<Result<Guid>>;
    private sealed record IntermediateSaveThenFailVoidCommand(Guid SavedId, Guid TrackedId) : ICommand<Result<VoidResult>>;
    private sealed record IntermediateSaveThenFailStringCommand(Guid SavedId, Guid TrackedId) : ICommand<Result<string>>;
    private sealed record IntermediateSaveThenThrowCommand(Guid SavedId) : ICommand<Result<VoidResult>>;
    private sealed record NestedTransactionInfo(Guid OuterTransactionId, Guid InnerTransactionId);
    private sealed record OuterSuccessWithNestedCommand(Guid OuterId, Guid InnerId) : ICommand<Result<NestedTransactionInfo>>;
    private sealed record InnerSaveClientCommand(Guid Id, string Name, bool ShouldFail) : ICommand<Result<Guid>>;
    private sealed record OuterBulkWithFailingInnerCommand(Guid OuterId, Guid InnerSuccessId, Guid InnerFailId) : ICommand<Result<VoidResult>>;
    private sealed record InnerMutateAndSaveThenFailCommand(Guid SeedId, Guid InnerId) : ICommand<Result<VoidResult>>;
    private sealed record OuterSwallowsInnerFailureCommand(Guid SeedId, Guid OuterId, Guid InnerId) : ICommand<Result<VoidResult>>;
    private sealed record BrokenTxTransientFailThenSucceedCommand(Guid Attempt1Id, Guid Attempt2Id) : ICommand<Result<Guid>>;

    private sealed class AddClientWithoutExplicitSaveHandler(DiscoveryDbContext db)
        : IRequestHandler<AddClientWithoutExplicitSaveCommand, Result<Guid>>
    {
        public Task<Result<Guid>> Handle(AddClientWithoutExplicitSaveCommand request, CancellationToken ct)
        {
            db.Clients.Add(new Client { Id = request.Id, Name = request.Name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            return Task.FromResult(Result<Guid>.Success(request.Id));
        }
    }

    private sealed class IntermediateSaveThenFailVoidHandler(DiscoveryDbContext db)
        : IRequestHandler<IntermediateSaveThenFailVoidCommand, Result<VoidResult>>
    {
        public async Task<Result<VoidResult>> Handle(IntermediateSaveThenFailVoidCommand request, CancellationToken ct)
        {
            db.Clients.Add(new Client { Id = request.SavedId, Name = "IntermediateSaved", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);

            db.Clients.Add(new Client { Id = request.TrackedId, Name = "TrackedOnly", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            return Result<VoidResult>.Failure(Error.Validation("domain.invalid", "Simulated failure"));
        }
    }

    private sealed class IntermediateSaveThenFailStringHandler(DiscoveryDbContext db)
        : IRequestHandler<IntermediateSaveThenFailStringCommand, Result<string>>
    {
        public async Task<Result<string>> Handle(IntermediateSaveThenFailStringCommand request, CancellationToken ct)
        {
            db.Clients.Add(new Client { Id = request.SavedId, Name = "StrSaved", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);

            db.Clients.Add(new Client { Id = request.TrackedId, Name = "StrTracked", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            return Result<string>.Failure(Error.Conflict("Simulated conflict"));
        }
    }

    private sealed class IntermediateSaveThenThrowHandler(DiscoveryDbContext db)
        : IRequestHandler<IntermediateSaveThenThrowCommand, Result<VoidResult>>
    {
        public async Task<Result<VoidResult>> Handle(IntermediateSaveThenThrowCommand request, CancellationToken ct)
        {
            db.Clients.Add(new Client { Id = request.SavedId, Name = "Thrown", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Boom");
        }
    }

    private sealed class InnerSaveClientHandler(DiscoveryDbContext db)
        : IRequestHandler<InnerSaveClientCommand, Result<Guid>>
    {
        public async Task<Result<Guid>> Handle(InnerSaveClientCommand request, CancellationToken ct)
        {
            var txId = db.Database.CurrentTransaction?.TransactionId ?? Guid.Empty;
            if (request.ShouldFail)
                return Result<Guid>.Failure(Error.Validation("inner.fail", "Inner failed"));

            db.Clients.Add(new Client { Id = request.Id, Name = request.Name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return Result<Guid>.Success(txId);
        }
    }

    private sealed class OuterSuccessWithNestedHandler(DiscoveryDbContext db, IMediator mediator)
        : IRequestHandler<OuterSuccessWithNestedCommand, Result<NestedTransactionInfo>>
    {
        public async Task<Result<NestedTransactionInfo>> Handle(OuterSuccessWithNestedCommand request, CancellationToken ct)
        {
            var outerTxId = db.Database.CurrentTransaction?.TransactionId ?? Guid.Empty;
            db.Clients.Add(new Client { Id = request.OuterId, Name = "Outer", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

            var innerResult = await mediator.Send(new InnerSaveClientCommand(request.InnerId, "Inner", ShouldFail: false), ct);
            if (innerResult.IsFailure)
                return Result<NestedTransactionInfo>.Failure(innerResult.Errors);

            return Result<NestedTransactionInfo>.Success(new NestedTransactionInfo(outerTxId, innerResult.Value));
        }
    }

    private sealed class OuterBulkWithFailingInnerHandler(DiscoveryDbContext db, IMediator mediator)
        : IRequestHandler<OuterBulkWithFailingInnerCommand, Result<VoidResult>>
    {
        public async Task<Result<VoidResult>> Handle(OuterBulkWithFailingInnerCommand request, CancellationToken ct)
        {
            db.Clients.Add(new Client { Id = request.OuterId, Name = "OuterBulk", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);

            var r1 = await mediator.Send(new InnerSaveClientCommand(request.InnerSuccessId, "InnerOk", ShouldFail: false), ct);
            if (r1.IsFailure)
                return Result<VoidResult>.Failure(r1.Errors);

            var r2 = await mediator.Send(new InnerSaveClientCommand(request.InnerFailId, "InnerFail", ShouldFail: true), ct);
            if (r2.IsFailure)
                return Result<VoidResult>.Failure(r2.Errors);

            return Result<VoidResult>.Success(VoidResult.Value);
        }
    }

    private sealed class InnerMutateAndSaveThenFailHandler(DiscoveryDbContext db)
        : IRequestHandler<InnerMutateAndSaveThenFailCommand, Result<VoidResult>>
    {
        public async Task<Result<VoidResult>> Handle(InnerMutateAndSaveThenFailCommand request, CancellationToken ct)
        {
            await db.Clients
                .Where(c => c.Id == request.SeedId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "InnerMutatedSeed"), ct);

            db.Clients.Add(new Client { Id = request.InnerId, Name = "InnerShouldRollback", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);

            return Result<VoidResult>.Failure(Error.Validation("inner.nonfatal", "Non-fatal inner failure"));
        }
    }

    private sealed class OuterSwallowsInnerFailureHandler(DiscoveryDbContext db, IMediator mediator)
        : IRequestHandler<OuterSwallowsInnerFailureCommand, Result<VoidResult>>
    {
        public async Task<Result<VoidResult>> Handle(OuterSwallowsInnerFailureCommand request, CancellationToken ct)
        {
            // Keep outerId pending (unsaved) across the nested SaveChangesAsync()+rollback to verify pre-existing pending state is preserved.
            db.Clients.Add(new Client { Id = request.OuterId, Name = "OuterCommitted", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

            var innerResult = await mediator.Send(new InnerMutateAndSaveThenFailCommand(request.SeedId, request.InnerId), ct);
            _ = innerResult.IsFailure; // Intentionally swallowed like ExecuteTicketRunbookCommandHandler.AttachEvidenceAsync

            return Result<VoidResult>.Success(VoidResult.Value);
        }
    }

    private sealed class BrokenTxTransientFailThenSucceedHandler(DiscoveryDbContext db, RetryTestState state)
        : IRequestHandler<BrokenTxTransientFailThenSucceedCommand, Result<Guid>>
    {
        public async Task<Result<Guid>> Handle(BrokenTxTransientFailThenSucceedCommand request, CancellationToken ct)
        {
            state.TrackerCountsAtAttemptStart.Add(db.ChangeTracker.Entries().Count());
            state.Attempts++;

            if (state.Attempts == 1)
            {
                db.Clients.Add(new Client { Id = request.Attempt1Id, Name = "Attempt1Dirty", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                // Complete/abort the underlying SqliteTransaction so EF's subsequent RollbackAsync throws InvalidOperationException.
                await db.Database.CurrentTransaction!.GetDbTransaction().RollbackAsync(ct);
                throw new TransientDbTestException("Simulated transient connection failure after transaction abort");
            }

            db.Clients.Add(new Client { Id = request.Attempt2Id, Name = "Attempt2Committed", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            return Result<Guid>.Success(request.Attempt2Id);
        }
    }

    private sealed class RetryTestState
    {
        public int Attempts { get; set; }
        public List<int> TrackerCountsAtAttemptStart { get; } = [];
    }

    private sealed class TransientDbTestException(string message) : Exception(message);

    private sealed class TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(10))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientDbTestException;
    }

    private sealed class TestRetryingExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
        : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new TestRetryingExecutionStrategy(dependencies);
    }

    private sealed class TransactionTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Client) };
            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null &&
                                     t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(entity => entity.HasKey(c => c.Id));
        }
    }
}

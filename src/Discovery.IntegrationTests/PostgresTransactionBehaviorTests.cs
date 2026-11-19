using Discovery.Core.Cqrs;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Discovery.IntegrationTests.Fixtures;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.IntegrationTests;

[TestFixture]
[Category("Integration")]
[Category("PostgreSQL")]
public sealed class PostgresTransactionBehaviorTests
{
    private DiscoveryPostgresWebApplicationFactory? _factory;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        await PostgresContainerFixture.EnsureAvailableOrIgnoreAsync();
        _factory = await DiscoveryPostgresWebApplicationFactory.CreateAsync(
            "txbehavior",
            services =>
            {
                services.AddTransient<IRequestHandler<SaveThenFailPgCommand, Result<VoidResult>>, SaveThenFailPgCommandHandler>();
                services.AddTransient<IRequestHandler<OuterWithNestedFailurePgCommand, Result<VoidResult>>, OuterWithNestedFailurePgCommandHandler>();
                services.AddTransient<IRequestHandler<NestedSuccessThenOuterFailPgCommand, Result<VoidResult>>, NestedSuccessThenOuterFailPgCommandHandler>();
                services.AddTransient<IRequestHandler<NestedSaveThenFailPgCommand, Result<VoidResult>>, NestedSaveThenFailPgCommandHandler>();
                services.AddTransient<IRequestHandler<NestedSaveAndSucceedPgCommand, Result<VoidResult>>, NestedSaveAndSucceedPgCommandHandler>();
            });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Test]
    public async Task TransactionBehavior_WhenCommandReturnsResultFailure_RollsBackRealPostgresWrites()
    {
        var savedId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

            var result = await mediator.Send(new SaveThenFailPgCommand(savedId, pendingId));

            Assert.That(result.IsFailure, Is.True);
            Assert.That(db.Database.CurrentTransaction, Is.Null);
            Assert.That(db.ChangeTracker.Entries(), Is.Empty);
        }

        await using (var verifyScope = _factory.Services.CreateAsyncScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var count = await verifyDb.Clients.AsNoTracking()
                .CountAsync(c => c.Id == savedId || c.Id == pendingId);
            Assert.That(count, Is.EqualTo(0), "Real PostgreSQL transaction must roll back intermediate SaveChangesAsync when Result.IsFailure.");
        }
    }

    [Test]
    public async Task TransactionBehavior_NestedFailureSavepoint_RollsBackInnerWhilePreservingOuterOnRealPostgres()
    {
        var outerId = Guid.NewGuid();
        var innerId = Guid.NewGuid();

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

            var result = await mediator.Send(new OuterWithNestedFailurePgCommand(outerId, innerId));

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(db.Database.CurrentTransaction, Is.Null);
        }

        await using (var verifyScope = _factory.Services.CreateAsyncScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var outerExists = await verifyDb.Clients.AsNoTracking().AnyAsync(c => c.Id == outerId);
            var innerExists = await verifyDb.Clients.AsNoTracking().AnyAsync(c => c.Id == innerId);

            Assert.That(outerExists, Is.True, "Outer PostgreSQL transaction must commit when outer command succeeds.");
            Assert.That(innerExists, Is.False, "Nested savepoint must roll back inner writes when nested command returns Result.Failure.");
        }
    }

    [Test]
    public async Task TransactionBehavior_NestedSuccessThenOuterFailure_RollsBackBothOnRealPostgres()
    {
        var outerId = Guid.NewGuid();
        var innerId = Guid.NewGuid();

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var result = await mediator.Send(new NestedSuccessThenOuterFailPgCommand(outerId, innerId));
            Assert.That(result.IsFailure, Is.True);
        }

        await using (var verifyScope = _factory.Services.CreateAsyncScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
            var count = await verifyDb.Clients.AsNoTracking()
                .CountAsync(c => c.Id == outerId || c.Id == innerId);
            Assert.That(count, Is.EqualTo(0), "Outer rollback must undo released nested savepoint writes in PostgreSQL.");
        }
    }

    public sealed record SaveThenFailPgCommand(Guid SavedId, Guid PendingId) : ICommand<Result<VoidResult>>;
    public sealed record OuterWithNestedFailurePgCommand(Guid OuterId, Guid InnerId) : ICommand<Result<VoidResult>>;
    public sealed record NestedSuccessThenOuterFailPgCommand(Guid OuterId, Guid InnerId) : ICommand<Result<VoidResult>>;
    public sealed record NestedSaveThenFailPgCommand(Guid InnerId) : ICommand<Result<VoidResult>>;
    public sealed record NestedSaveAndSucceedPgCommand(Guid InnerId) : ICommand<Result<VoidResult>>;

    private sealed class SaveThenFailPgCommandHandler(DiscoveryDbContext db)
        : IRequestHandler<SaveThenFailPgCommand, Result<VoidResult>>
    {
        public async Task<Result<VoidResult>> Handle(SaveThenFailPgCommand request, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            db.Clients.Add(new Client { Id = request.SavedId, Name = $"PG-Saved-{request.SavedId:N}", IsActive = true, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(ct);

            db.Clients.Add(new Client { Id = request.PendingId, Name = $"PG-Pending-{request.PendingId:N}", IsActive = true, CreatedAt = now, UpdatedAt = now });
            return Result<VoidResult>.Failure(Error.Validation("test", "Simulated business failure after SaveChangesAsync."));
        }
    }

    private sealed class OuterWithNestedFailurePgCommandHandler(DiscoveryDbContext db, IMediator mediator)
        : IRequestHandler<OuterWithNestedFailurePgCommand, Result<VoidResult>>
    {
        public async Task<Result<VoidResult>> Handle(OuterWithNestedFailurePgCommand request, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            db.Clients.Add(new Client { Id = request.OuterId, Name = $"PG-Outer-{request.OuterId:N}", IsActive = true, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(ct);

            var nestedResult = await mediator.Send(new NestedSaveThenFailPgCommand(request.InnerId), ct);
            Assert.That(nestedResult.IsFailure, Is.True);

            return Result<VoidResult>.Success(VoidResult.Value);
        }
    }

    private sealed class NestedSuccessThenOuterFailPgCommandHandler(DiscoveryDbContext db, IMediator mediator)
        : IRequestHandler<NestedSuccessThenOuterFailPgCommand, Result<VoidResult>>
    {
        public async Task<Result<VoidResult>> Handle(NestedSuccessThenOuterFailPgCommand request, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            db.Clients.Add(new Client { Id = request.OuterId, Name = $"PG-OuterFail-{request.OuterId:N}", IsActive = true, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(ct);

            var nestedResult = await mediator.Send(new NestedSaveAndSucceedPgCommand(request.InnerId), ct);
            Assert.That(nestedResult.IsSuccess, Is.True);

            return Result<VoidResult>.Failure(Error.Validation("test", "Outer failure rolls back released savepoint too."));
        }
    }

    private sealed class NestedSaveThenFailPgCommandHandler(DiscoveryDbContext db)
        : IRequestHandler<NestedSaveThenFailPgCommand, Result<VoidResult>>
    {
        public async Task<Result<VoidResult>> Handle(NestedSaveThenFailPgCommand request, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            db.Clients.Add(new Client { Id = request.InnerId, Name = $"PG-InnerFail-{request.InnerId:N}", IsActive = true, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(ct);
            return Result<VoidResult>.Failure(Error.Validation("test", "Nested savepoint failure."));
        }
    }

    private sealed class NestedSaveAndSucceedPgCommandHandler(DiscoveryDbContext db)
        : IRequestHandler<NestedSaveAndSucceedPgCommand, Result<VoidResult>>
    {
        public async Task<Result<VoidResult>> Handle(NestedSaveAndSucceedPgCommand request, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            db.Clients.Add(new Client { Id = request.InnerId, Name = $"PG-InnerOk-{request.InnerId:N}", IsActive = true, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(ct);
            return Result<VoidResult>.Success(VoidResult.Value);
        }
    }
}

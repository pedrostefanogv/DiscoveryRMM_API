using Discovery.Core.Cqrs;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Behaviors;

/// <summary>
/// Pipeline behavior that wraps command execution in a database transaction.
/// Only applies to ICommand requests.
/// Automatically saves changes and commits on success, rolls back on failure.
/// Uses savepoints to isolate nested commands within an ambient transaction.
/// </summary>
public sealed class TransactionBehavior<TRequest, TResponse>(
    DiscoveryDbContext dbContext,
    ILogger<TransactionBehavior<TRequest, TResponse>> logger
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICommand<TResponse>
    where TResponse : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is { } ambientTransaction)
        {
            return await HandleNestedAsync(ambientTransaction, next, cancellationToken);
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken);

            var shouldClearTracker = false;
            try
            {
                var response = await next();

                if (response is ICqrsResult { IsFailure: true })
                {
                    logger.LogDebug(
                        "Transaction rolled back due to failure result for {RequestName}",
                        typeof(TRequest).Name);
                    shouldClearTracker = true;
                    await SafeRollbackAsync(transaction);
                    return response;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Transaction rolled back for {RequestName}",
                    typeof(TRequest).Name);
                shouldClearTracker = true;
                await SafeRollbackAsync(transaction);
                throw;
            }
            finally
            {
                if (shouldClearTracker)
                {
                    dbContext.ChangeTracker.Clear();
                }
            }
        });
    }

    private async Task<TResponse> HandleNestedAsync(
        IDbContextTransaction ambientTransaction,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!ambientTransaction.SupportsSavepoints)
        {
            return await next();
        }

        var preSnapshot = SnapshotTrackedEntries();
        var savepointName = $"tx_nested_{Guid.NewGuid():N}";
        await ambientTransaction.CreateSavepointAsync(savepointName, cancellationToken);

        try
        {
            var response = await next();

            if (response is ICqrsResult { IsFailure: true })
            {
                logger.LogDebug(
                    "Savepoint {SavepointName} rolled back due to failure result for nested {RequestName}",
                    savepointName, typeof(TRequest).Name);
                await SafeRollbackToSavepointAsync(ambientTransaction, savepointName);
                RestoreTrackedEntries(preSnapshot);
                return response;
            }

            await SafeReleaseSavepointAsync(ambientTransaction, savepointName, cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Savepoint {SavepointName} rolled back due to exception for nested {RequestName}",
                savepointName, typeof(TRequest).Name);
            await SafeRollbackToSavepointAsync(ambientTransaction, savepointName);
            RestoreTrackedEntries(preSnapshot);
            throw;
        }
    }

    private async Task SafeRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rbEx)
        {
            logger.LogWarning(
                rbEx,
                "Rollback failed for {RequestName}",
                typeof(TRequest).Name);
        }
    }

    private async Task SafeRollbackToSavepointAsync(
        IDbContextTransaction transaction,
        string savepointName)
    {
        try
        {
            await transaction.RollbackToSavepointAsync(savepointName, CancellationToken.None);
        }
        catch (Exception rbEx)
        {
            logger.LogWarning(
                rbEx,
                "Savepoint rollback failed for {SavepointName} on {RequestName}",
                savepointName, typeof(TRequest).Name);
        }
    }

    private async Task SafeReleaseSavepointAsync(
        IDbContextTransaction transaction,
        string savepointName,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.ReleaseSavepointAsync(savepointName, cancellationToken);
        }
        catch (NotSupportedException ex)
        {
            logger.LogDebug(
                ex,
                "Savepoint release not supported by provider for {SavepointName}",
                savepointName);
        }
    }

    private sealed record TrackedEntrySnapshot(
        EntityState State,
        PropertyValues OriginalValues,
        PropertyValues CurrentValues,
        IReadOnlyList<string> ModifiedProperties);

    private Dictionary<object, TrackedEntrySnapshot> SnapshotTrackedEntries()
    {
        var snapshot = new Dictionary<object, TrackedEntrySnapshot>(ReferenceEqualityComparer.Instance);
        foreach (var entry in dbContext.ChangeTracker.Entries())
        {
            var modifiedProps = entry.State == EntityState.Modified
                ? entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToArray()
                : Array.Empty<string>();

            snapshot[entry.Entity] = new TrackedEntrySnapshot(
                entry.State,
                entry.OriginalValues.Clone(),
                entry.CurrentValues.Clone(),
                modifiedProps);
        }

        return snapshot;
    }

    private void RestoreTrackedEntries(Dictionary<object, TrackedEntrySnapshot> preSnapshot)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            if (!preSnapshot.ContainsKey(entry.Entity))
            {
                entry.State = EntityState.Detached;
            }
        }

        foreach (var (entity, snap) in preSnapshot)
        {
            var entry = dbContext.Entry(entity);
            if (entry.State == EntityState.Detached)
            {
                entry.State = snap.State == EntityState.Modified ? EntityState.Unchanged : snap.State;
            }

            entry.OriginalValues.SetValues(snap.OriginalValues);
            entry.CurrentValues.SetValues(snap.CurrentValues);

            if (snap.State == EntityState.Modified)
            {
                if (snap.ModifiedProperties.Count > 0)
                {
                    entry.State = EntityState.Unchanged;
                    foreach (var propName in snap.ModifiedProperties)
                    {
                        entry.Property(propName).IsModified = true;
                    }
                }
                else
                {
                    entry.State = EntityState.Modified;
                }
            }
            else
            {
                entry.State = snap.State;
            }
        }
    }
}
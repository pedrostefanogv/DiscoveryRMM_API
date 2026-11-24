using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class ReportExecutionRepository : IReportExecutionRepository
{
    private readonly DiscoveryDbContext _db;

    public ReportExecutionRepository(DiscoveryDbContext db) => _db = db;

    public async Task<ReportExecution> CreateAsync(ReportExecution execution)
    {
        execution.Id = IdGenerator.NewId();
        execution.CreatedAt = DateTime.UtcNow;

        _db.ReportExecutions.Add(execution);
        await _db.SaveChangesAsync();

        return execution;
    }

    public async Task<ReportExecution?> GetByIdAsync(Guid id, Guid? clientId = null)
    {
        return await _db.ReportExecutions
            .AsNoTracking()
            .Where(execution => execution.Id == id)
            .Where(execution => !clientId.HasValue || execution.ClientId == clientId.Value)
            .FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<ReportExecution>> GetRecentByClientAsync(Guid? clientId = null, int limit = 50)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        var query = _db.ReportExecutions.AsNoTracking();

        if (clientId.HasValue)
            query = query.Where(execution => execution.ClientId == clientId.Value);

        return await query
            .OrderByDescending(execution => execution.CreatedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ReportExecution>> GetRecentByClientIdsAsync(IReadOnlyCollection<Guid> clientIds, int limit = 50)
    {
        if (clientIds.Count == 0)
            return [];

        var safeLimit = Math.Clamp(limit, 1, 200);

        return await _db.ReportExecutions
            .AsNoTracking()
            .Where(execution => execution.ClientId != null && clientIds.Contains(execution.ClientId.Value))
            .OrderByDescending(execution => execution.CreatedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ReportExecution>> GetPendingAsync(int limit = 20)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        return await _db.ReportExecutions
            .AsNoTracking()
            .Where(execution => execution.Status == ReportExecutionStatus.Pending)
            .OrderBy(execution => execution.CreatedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ReportExecution>> GetExpiredAsync(DateTime cutoff, int limit = 1000)
    {
        var safeLimit = Math.Clamp(limit, 1, 5000);

        return await _db.ReportExecutions
            .AsNoTracking()
            .Where(execution => execution.CreatedAt <= cutoff)
            .OrderBy(execution => execution.CreatedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
            return 0;

        var deleted = await _db.ReportExecutions
            .Where(execution => ids.Contains(execution.Id))
            .ExecuteDeleteAsync();

        return deleted;
    }

    public async Task<bool> TryClaimPendingAsync(Guid id, Guid? clientId = null)
    {
        var claimedAt = DateTime.UtcNow;
        var claimed = await _db.ReportExecutions
            .Where(execution => execution.Id == id)
            .Where(execution => !clientId.HasValue || execution.ClientId == clientId.Value)
            .Where(execution => execution.Status == ReportExecutionStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(execution => execution.Status, ReportExecutionStatus.Running)
                .SetProperty(execution => execution.StartedAt, claimedAt)
                .SetProperty(execution => execution.AttemptCount, execution => execution.AttemptCount + 1)
                .SetProperty(execution => execution.ErrorMessage, (string?)null));

        return claimed == 1;
    }

    public async Task<int> RequeueStaleRunningAsync(DateTime startedBefore, int limit = 100)
    {
        const int maxAttempts = 3;
        var safeLimit = Math.Clamp(limit, 1, 1000);

        // ExecuteUpdate nao aceita ORDER BY/LIMIT de forma portavel: seleciona os
        // ids primeiro e depois atualiza.
        var stale = await _db.ReportExecutions
            .AsNoTracking()
            .Where(execution => execution.Status == ReportExecutionStatus.Running)
            .Where(execution => execution.StartedAt != null && execution.StartedAt <= startedBefore)
            .OrderBy(execution => execution.StartedAt)
            .Take(safeLimit)
            .Select(execution => new { execution.Id, execution.AttemptCount })
            .ToListAsync();

        if (stale.Count == 0)
            return 0;

        var poisonIds = stale.Where(x => x.AttemptCount >= maxAttempts).Select(x => x.Id).ToList();
        if (poisonIds.Count > 0)
        {
            var finishedAt = DateTime.UtcNow;
            await _db.ReportExecutions
                .Where(execution => poisonIds.Contains(execution.Id))
                .Where(execution => execution.Status == ReportExecutionStatus.Running)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(execution => execution.Status, ReportExecutionStatus.Failed)
                    .SetProperty(execution => execution.FinishedAt, finishedAt)
                    .SetProperty(execution => execution.ErrorMessage, "Execucao excedeu o limite maximo de tentativas apos interrompimento."));
        }

        var retryIds = stale.Where(x => x.AttemptCount < maxAttempts).Select(x => x.Id).ToList();
        if (retryIds.Count == 0)
            return 0;

        return await _db.ReportExecutions
            .Where(execution => retryIds.Contains(execution.Id))
            .Where(execution => execution.Status == ReportExecutionStatus.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(execution => execution.Status, ReportExecutionStatus.Pending)
                .SetProperty(execution => execution.StartedAt, (DateTime?)null)
                .SetProperty(execution => execution.ErrorMessage, (string?)null));
    }

    public async Task ClearStorageFieldsAsync(Guid id)
    {
        await _db.ReportExecutions
            .Where(execution => execution.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(execution => execution.StorageObjectKey, (string?)null)
                .SetProperty(execution => execution.StorageBucket, (string?)null)
                .SetProperty(execution => execution.StoragePresignedUrl, (string?)null)
                .SetProperty(execution => execution.StoragePresignedUrlGeneratedAt, (DateTime?)null));
    }

    public async Task UpdateStatusAsync(Guid id, Guid? clientId, ReportExecutionStatus status, string? errorMessage = null)
    {
        var execution = await _db.ReportExecutions
            .FirstOrDefaultAsync(item => item.Id == id && (!clientId.HasValue || item.ClientId == clientId.Value));

        if (execution is null)
            throw new InvalidOperationException($"Report execution {id} not found.");

        execution.Status = status;
        execution.ErrorMessage = errorMessage;

        if (status == ReportExecutionStatus.Running)
            execution.StartedAt = DateTime.UtcNow;

        if (status is ReportExecutionStatus.Completed or ReportExecutionStatus.Failed or ReportExecutionStatus.Cancelled)
            execution.FinishedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task UpdateResultAsync(
        Guid id,
        Guid? clientId,
        string storageObjectKey,
        string storageBucket,
        string storageContentType,
        long storageSizeBytes,
        string? storageChecksum,
        int storageProviderType,
        int rowCount,
        int executionTimeMs)
    {
        var execution = await _db.ReportExecutions
            .FirstOrDefaultAsync(item => item.Id == id && (!clientId.HasValue || item.ClientId == clientId.Value));

        if (execution is null)
            throw new InvalidOperationException($"Report execution {id} not found.");

        execution.StorageObjectKey = storageObjectKey;
        execution.StorageBucket = storageBucket;
        execution.StorageContentType = storageContentType;
        execution.StorageSizeBytes = storageSizeBytes;
        execution.StorageChecksum = storageChecksum;
        execution.StorageProviderType = storageProviderType;
        execution.RowCount = rowCount;
        execution.ExecutionTimeMs = executionTimeMs;
        execution.Status = ReportExecutionStatus.Completed;
        execution.FinishedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }
}

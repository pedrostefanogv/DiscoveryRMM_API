using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class ReportExecutionRepository : IReportExecutionRepository
{
    private readonly MeduzaDbContext _db;

    public ReportExecutionRepository(MeduzaDbContext db) => _db = db;

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

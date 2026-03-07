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

    public async Task<ReportExecution?> GetByIdAsync(Guid id, Guid clientId)
    {
        return await _db.ReportExecutions
            .AsNoTracking()
            .Where(execution => execution.Id == id && execution.ClientId == clientId)
            .FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<ReportExecution>> GetRecentByClientAsync(Guid clientId, int limit = 50)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        return await _db.ReportExecutions
            .AsNoTracking()
            .Where(execution => execution.ClientId == clientId)
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

    public async Task UpdateStatusAsync(Guid id, Guid clientId, ReportExecutionStatus status, string? errorMessage = null)
    {
        var execution = await _db.ReportExecutions
            .FirstOrDefaultAsync(item => item.Id == id && item.ClientId == clientId);

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

    public async Task UpdateResultAsync(Guid id, Guid clientId, string resultPath, string resultContentType, long resultSizeBytes, int rowCount, int executionTimeMs)
    {
        var execution = await _db.ReportExecutions
            .FirstOrDefaultAsync(item => item.Id == id && item.ClientId == clientId);

        if (execution is null)
            throw new InvalidOperationException($"Report execution {id} not found.");

        execution.ResultPath = resultPath;
        execution.ResultContentType = resultContentType;
        execution.ResultSizeBytes = resultSizeBytes;
        execution.RowCount = rowCount;
        execution.ExecutionTimeMs = executionTimeMs;
        execution.Status = ReportExecutionStatus.Completed;
        execution.FinishedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }
}

using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AutomationExecutionReportRepository : IAutomationExecutionReportRepository
{
    private readonly MeduzaDbContext _db;

    public AutomationExecutionReportRepository(MeduzaDbContext db) => _db = db;

    public async Task<AutomationExecutionReport> CreateAsync(AutomationExecutionReport report)
    {
        report.Id = IdGenerator.NewId();
        report.CreatedAt = DateTime.UtcNow;
        report.UpdatedAt = DateTime.UtcNow;
        _db.AutomationExecutionReports.Add(report);
        await _db.SaveChangesAsync();
        return report;
    }

    public async Task<AutomationExecutionReport?> GetByCommandIdAsync(Guid commandId)
    {
        return await _db.AutomationExecutionReports
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommandId == commandId);
    }

    public async Task<IReadOnlyList<AutomationExecutionReport>> GetByAgentIdAsync(Guid agentId, int limit = 100)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        return await _db.AutomationExecutionReports
            .AsNoTracking()
            .Where(x => x.AgentId == agentId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task UpdateAckAsync(Guid commandId, Guid? taskId, Guid? scriptId, string? ackMetadataJson, DateTime acknowledgedAt, string? correlationId)
    {
        await _db.AutomationExecutionReports
            .Where(x => x.CommandId == commandId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.TaskId, _ => taskId)
                .SetProperty(x => x.ScriptId, _ => scriptId)
                .SetProperty(x => x.AckMetadataJson, _ => ackMetadataJson)
                .SetProperty(x => x.AcknowledgedAt, _ => acknowledgedAt)
                .SetProperty(x => x.Status, _ => AutomationExecutionStatus.Acknowledged)
                .SetProperty(x => x.CorrelationId, x => correlationId ?? x.CorrelationId)
                .SetProperty(x => x.UpdatedAt, _ => DateTime.UtcNow));
    }

    public async Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId)
    {
        await _db.AutomationExecutionReports
            .Where(x => x.CommandId == commandId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.TaskId, _ => taskId)
                .SetProperty(x => x.ScriptId, _ => scriptId)
                .SetProperty(x => x.ResultMetadataJson, _ => resultMetadataJson)
                .SetProperty(x => x.ResultReceivedAt, _ => resultReceivedAt)
                .SetProperty(x => x.ExitCode, _ => exitCode)
                .SetProperty(x => x.ErrorMessage, _ => errorMessage)
                .SetProperty(x => x.Status, _ => success ? AutomationExecutionStatus.Completed : AutomationExecutionStatus.Failed)
                .SetProperty(x => x.CorrelationId, x => correlationId ?? x.CorrelationId)
                .SetProperty(x => x.UpdatedAt, _ => DateTime.UtcNow));
    }
}

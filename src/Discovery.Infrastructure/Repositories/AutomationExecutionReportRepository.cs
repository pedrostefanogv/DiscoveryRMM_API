using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.DTOs;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AutomationExecutionReportRepository : IAutomationExecutionReportRepository
{
    private readonly DiscoveryDbContext _db;
    private readonly IAgentRepository _agentRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentMessaging _messaging;

    public AutomationExecutionReportRepository(
        DiscoveryDbContext db,
        IAgentRepository agentRepository,
        ISiteRepository siteRepository,
        IAgentMessaging messaging)
    {
        _db = db;
        _agentRepository = agentRepository;
        _siteRepository = siteRepository;
        _messaging = messaging;
    }

    public async Task<AutomationExecutionReport> CreateAsync(AutomationExecutionReport report)
    {
        report.Id = IdGenerator.NewId();
        report.CreatedAt = DateTime.UtcNow;
        report.UpdatedAt = DateTime.UtcNow;
        _db.AutomationExecutionReports.Add(report);
        await _db.SaveChangesAsync();
        await PublishDashboardEventAsync("AutomationExecutionCreated", report.AgentId, report.CommandId, report.Status, report.TaskId, report.ScriptId, report.CorrelationId);
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
        var report = await _db.AutomationExecutionReports
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommandId == commandId);

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

        if (report is not null)
            await PublishDashboardEventAsync("AutomationExecutionAcknowledged", report.AgentId, commandId, AutomationExecutionStatus.Acknowledged, taskId, scriptId, correlationId ?? report.CorrelationId);
    }

    public async Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId)
    {
        var report = await _db.AutomationExecutionReports
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommandId == commandId);

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

        if (report is not null)
        {
            var finalStatus = success ? AutomationExecutionStatus.Completed : AutomationExecutionStatus.Failed;
            await PublishDashboardEventAsync("AutomationExecutionResult", report.AgentId, commandId, finalStatus, taskId, scriptId, correlationId ?? report.CorrelationId);
        }
    }

    private async Task PublishDashboardEventAsync(
        string eventType,
        Guid agentId,
        Guid commandId,
        AutomationExecutionStatus status,
        Guid? taskId,
        Guid? scriptId,
        string? correlationId)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        Guid? siteId = agent?.SiteId;
        Guid? clientId = null;

        if (siteId.HasValue)
        {
            var site = await _siteRepository.GetByIdAsync(siteId.Value);
            clientId = site?.ClientId;
        }

        await _messaging.PublishDashboardEventAsync(
            DashboardEventMessage.Create(
                eventType,
                new
                {
                    agentId,
                    commandId,
                    status = status.ToString(),
                    taskId,
                    scriptId,
                    correlationId
                },
                clientId,
                siteId));
    }
}

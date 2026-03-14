using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAutomationExecutionReportRepository
{
    Task<AutomationExecutionReport> CreateAsync(AutomationExecutionReport report);
    Task<AutomationExecutionReport?> GetByCommandIdAsync(Guid commandId);
    Task<IReadOnlyList<AutomationExecutionReport>> GetByAgentIdAsync(Guid agentId, int limit = 100);
    Task UpdateAckAsync(Guid commandId, Guid? taskId, Guid? scriptId, string? ackMetadataJson, DateTime acknowledgedAt, string? correlationId);
    Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId);
}

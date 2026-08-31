using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAutomationExecutionReportRepository
{
    Task<AutomationExecutionReport> CreateAsync(AutomationExecutionReport report);
    Task<AutomationExecutionReport?> GetByCommandIdAsync(Guid commandId);
    Task<IReadOnlyList<AutomationExecutionReport>> GetByAgentIdAsync(Guid agentId, int limit = 100);
    Task<IReadOnlyList<AutomationExecutionReport>> GetByTaskIdAsync(Guid taskId, int limit = 100);
    Task UpdateAckAsync(Guid commandId, Guid? taskId, Guid? scriptId, string? ackMetadataJson, DateTime acknowledgedAt, string? correlationId);
    Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId);

    /// <summary>
    /// Cria (ou atualiza) o registro de execução para execuções automáticas reportadas
    /// pelo agent via policy-sync (commandId gerado pelo próprio agent, sem comando dispatchado).
    /// </summary>
    Task UpsertPolicyExecutionAsync(Guid agentId, Guid commandId, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, AutomationExecutionStatus status, string? correlationId);
}

using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAutomationExecutionReportRepository
{
    Task<AutomationExecutionReport> CreateAsync(AutomationExecutionReport report);
    Task<AutomationExecutionReport?> GetByCommandIdAsync(Guid commandId);
    Task<AutomationExecutionReport?> GetByIdAsync(Guid id);

    /// <summary>
    /// Marca a execução como <see cref="AutomationExecutionStatus.Cancelled"/>
    /// apenas se ela ainda não estiver em estado terminal. Retorna false quando
    /// nada foi alterado (corrida com a conclusão do comando).
    /// </summary>
    Task<bool> MarkCancelledAsync(Guid executionId, string? errorMessage, DateTime cancelledAt);
    /// <summary>
    /// Histórico recente do agent. Os filtros opcionais combinam em AND e são
    /// aplicados no banco (não em memória), para não trazer linhas descartadas.
    /// </summary>
    Task<IReadOnlyList<AutomationExecutionReport>> GetByAgentIdAsync(
        Guid agentId,
        int limit = 100,
        AutomationExecutionStatus? status = null,
        AutomationExecutionSourceType? sourceType = null,
        Guid? taskId = null,
        Guid? scriptId = null,
        string? correlationId = null);
    Task<IReadOnlyList<AutomationExecutionReport>> GetByTaskIdAsync(Guid taskId, int limit = 100);
    Task UpdateAckAsync(Guid commandId, Guid? taskId, Guid? scriptId, string? ackMetadataJson, DateTime acknowledgedAt, string? correlationId);
    Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId);

    /// <summary>
    /// Atualiza apenas o resultado de um report já existente (sem tocar em
    /// task/script). Usado pelo handler de resultado de comando (NATS), que não
    /// conhece o vínculo do report. Idempotente para estado terminal.
    /// </summary>
    Task UpdateResultFromCommandAsync(Guid commandId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt);

    /// <summary>
    /// Cria (ou atualiza) o registro de execução para execuções automáticas reportadas
    /// pelo agent via policy-sync (commandId gerado pelo próprio agent, sem comando dispatchado).
    /// </summary>
    Task UpsertPolicyExecutionAsync(Guid agentId, Guid commandId, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, AutomationExecutionStatus status, string? correlationId);
}

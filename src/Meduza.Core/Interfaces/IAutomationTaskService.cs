using Meduza.Core.DTOs;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IAutomationTaskService
{
    Task<AutomationTaskPageDto> GetListAsync(
        AppApprovalScopeType? scopeType,
        Guid? scopeId,
        bool activeOnly,
        bool deletedOnly,
        bool includeDeleted,
        string? search,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        IReadOnlyList<AppApprovalScopeType>? scopeTypes,
        IReadOnlyList<AutomationTaskActionType>? actionTypes,
        IReadOnlyList<string>? labels,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);
    Task<AutomationTaskDetailDto?> GetByIdAsync(Guid id, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<AutomationTaskDetailDto> CreateAsync(CreateAutomationTaskRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default);
    Task<AutomationTaskDetailDto?> UpdateAsync(Guid id, UpdateAutomationTaskRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, string? reason, CancellationToken cancellationToken = default);
    Task<AutomationTaskDetailDto?> RestoreAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, string? reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AutomationTaskAuditDto>> GetAuditAsync(Guid id, int limit = 100, CancellationToken cancellationToken = default);
    Task<AutomationTaskTargetPreviewPageDto?> PreviewTargetAgentsAsync(Guid taskId, int limit = 50, int offset = 0, CancellationToken cancellationToken = default);
    Task<string> GetPolicyFingerprintForAgentAsync(Guid agentId, CancellationToken cancellationToken = default);
    Task<AgentAutomationPolicySyncResponse> SyncPolicyForAgentAsync(Guid agentId, AgentAutomationPolicySyncRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default);
}

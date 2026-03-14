using Meduza.Core.DTOs;
using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface IAutomationTaskService
{
    Task<AutomationTaskPageDto> GetListAsync(AppApprovalScopeType? scopeType, Guid? scopeId, bool activeOnly, int limit, int offset, CancellationToken cancellationToken = default);
    Task<AutomationTaskDetailDto?> GetByIdAsync(Guid id, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<AutomationTaskDetailDto> CreateAsync(CreateAutomationTaskRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default);
    Task<AutomationTaskDetailDto?> UpdateAsync(Guid id, UpdateAutomationTaskRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, string? reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AutomationTaskAuditDto>> GetAuditAsync(Guid id, int limit = 100, CancellationToken cancellationToken = default);
    Task<AgentAutomationPolicySyncResponse> SyncPolicyForAgentAsync(Guid agentId, AgentAutomationPolicySyncRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default);
}

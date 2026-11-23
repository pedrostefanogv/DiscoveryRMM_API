using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAutomationScriptService
{
    Task<CursorPageDto<AutomationScriptSummaryDto>> GetListPageAsync(
        Guid? clientId,
        bool activeOnly,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<CursorPageDto<AutomationScriptSummaryDto>> GetListPageAsync(
        Guid? clientId,
        bool activeOnly,
        string? cursor,
        int limit,
        CancellationToken cancellationToken,
        string? search,
        AutomationScriptType? scriptType)
        => GetListPageAsync(clientId, activeOnly, cursor, limit, cancellationToken);
    Task<AutomationScriptDetailDto?> GetByIdAsync(Guid id, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<AutomationScriptDetailDto> CreateAsync(CreateAutomationScriptRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default);
    Task<AutomationScriptDetailDto?> UpdateAsync(Guid id, UpdateAutomationScriptRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, string? reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AutomationScriptAuditDto>> GetAuditAsync(Guid id, int limit = 100, CancellationToken cancellationToken = default);
    Task<AutomationScriptConsumeDto?> GetConsumePayloadAsync(Guid id, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default);
}

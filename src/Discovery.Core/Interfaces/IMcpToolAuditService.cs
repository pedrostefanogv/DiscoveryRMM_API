using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public sealed record McpToolAuditQueryResult(
    IReadOnlyList<McpToolAuditLog> Items,
    int TotalCount,
    int Page,
    int PageSize);

public interface IMcpToolAuditService
{
    Task RecordAsync(McpToolAuditLog entry, CancellationToken ct = default);

    Task<McpToolAuditQueryResult> QueryAsync(
        Guid? clientId = null,
        Guid? agentId = null,
        string? toolName = null,
        string? status = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default);
}

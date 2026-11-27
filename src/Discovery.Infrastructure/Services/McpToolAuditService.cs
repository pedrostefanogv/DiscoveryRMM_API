using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public sealed class McpToolAuditService(
    DiscoveryDbContext db,
    ILogger<McpToolAuditService> logger) : IMcpToolAuditService
{
    public async Task RecordAsync(McpToolAuditLog entry, CancellationToken ct = default)
    {
        try
        {
            if (entry.ErrorMessage is { Length: > 500 })
                entry.ErrorMessage = entry.ErrorMessage[..500];

            db.McpToolAuditLogs.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Auditoria é best-effort: nunca derruba a execução da tool ou do chat.
            logger.LogWarning(ex, "Falha ao gravar auditoria MCP para tool {ToolName}", entry.ToolName);
        }
    }

    public async Task<McpToolAuditQueryResult> QueryAsync(
        Guid? clientId = null,
        Guid? agentId = null,
        string? toolName = null,
        string? status = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.McpToolAuditLogs.AsNoTracking().AsQueryable();

        if (clientId.HasValue)
            query = query.Where(x => x.ClientId == clientId.Value);
        if (agentId.HasValue)
            query = query.Where(x => x.AgentId == agentId.Value);
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            var term = toolName.Trim();
            query = db.Database.IsNpgsql()
                ? query.Where(x => EF.Functions.ILike(x.ToolName, $"%{term}%"))
                : query.Where(x => x.ToolName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status == status.Trim());
        if (fromUtc.HasValue)
            query = query.Where(x => x.CreatedAt >= fromUtc.Value);
        if (toUtc.HasValue)
            query = query.Where(x => x.CreatedAt <= toUtc.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new McpToolAuditQueryResult(items, total, page, pageSize);
    }
}

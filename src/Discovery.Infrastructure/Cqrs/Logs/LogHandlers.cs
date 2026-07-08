using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Logs.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Logs;

public sealed class ListLogsQueryHandler(
    ILogRepository repo
) : IRequestHandler<ListLogsQuery, Result<LogsPageDto>>
{
    public async Task<Result<LogsPageDto>> Handle(ListLogsQuery q, CancellationToken ct)
    {
        var query = new LogQuery
        {
            AgentId = q.AgentId is not null && Guid.TryParse(q.AgentId, out var aid) ? aid : null,
            SiteId = q.SiteId is not null && Guid.TryParse(q.SiteId, out var sid) ? sid : null,
            ClientId = q.ClientId is not null && Guid.TryParse(q.ClientId, out var cid) ? cid : null,
            Limit = q.Limit
        };

        if (!string.IsNullOrWhiteSpace(q.Cursor) && Guid.TryParse(q.Cursor, out var cursorId))
            query.CursorId = cursorId;

        var entries = await repo.QueryPageAsync(query);
        var items = entries.Select(e => new LogDto(
            e.Id, e.Level.ToString(), e.Type.ToString(), e.Source.ToString(),
            e.Message, e.CreatedAt)).ToList().AsReadOnly();

        var hasMore = items.Count >= q.Limit;
        var nextCursor = hasMore && items.Count > 0 ? items[^1].Id.ToString() : null;

        return Result<LogsPageDto>.Success(new LogsPageDto(items, nextCursor, hasMore));
    }
}

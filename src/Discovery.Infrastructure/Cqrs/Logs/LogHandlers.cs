using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Logs.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Logs;

public sealed class ListLogsQueryHandler(
    ILogRepository repo
) : IRequestHandler<ListLogsQuery, Result<CursorPageDto<LogDto>>>
{
    public async Task<Result<CursorPageDto<LogDto>>> Handle(ListLogsQuery q, CancellationToken ct)
    {
        var query = new LogQuery
        {
            AgentId = q.AgentId is not null && Guid.TryParse(q.AgentId, out var aid) ? aid : null,
            SiteId = q.SiteId is not null && Guid.TryParse(q.SiteId, out var sid) ? sid : null,
            ClientId = q.ClientId is not null && Guid.TryParse(q.ClientId, out var cid) ? cid : null,
            Limit = Math.Clamp(q.Limit, 1, 500),
            Level = q.Level.HasValue ? (LogLevel)q.Level.Value : null,
            Type = q.Type.HasValue ? (LogType)q.Type.Value : null,
            Source = q.Source.HasValue ? (LogSource)q.Source.Value : null,
            PeriodPreset = q.Period,
            SearchText = q.Search
        };

        if (!string.IsNullOrWhiteSpace(q.Cursor))
        {
            var parts = q.Cursor.Split('|');
            if (parts.Length == 2
                && long.TryParse(parts[0], out var ticks)
                && Guid.TryParse(parts[1], out var cursorId))
            {
                query.CursorCreatedAtUtc = new DateTime(ticks, DateTimeKind.Utc);
                query.CursorId = cursorId;
            }
        }

        if (!string.IsNullOrWhiteSpace(q.Period))
            ApplyPeriodPreset(query, q.Period);

        var entries = await repo.QueryPageAsync(query);
        var items = entries
            .Take(q.Limit)
            .Select(e => new LogDto(
                e.Id,
                e.Level.ToString(),
                e.Type.ToString(),
                e.Source.ToString(),
                e.Message,
                e.CreatedAt,
                e.ClientId,
                e.SiteId,
                e.AgentId,
                e.DataJson))
            .ToList().AsReadOnly();

        var hasMore = entries.Count > q.Limit;
        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextCursor = $"{last.CreatedAt.Ticks}|{last.Id}";
        }

        return Result<CursorPageDto<LogDto>>.Success(new CursorPageDto<LogDto>(
            items,
            items.Count,
            q.Cursor,
            nextCursor,
            hasMore,
            q.Limit));
    }

    private static void ApplyPeriodPreset(LogQuery query, string period)
    {
        var now = DateTime.UtcNow;
        switch (period.ToLowerInvariant())
        {
            case "1h":
                query.From = now.AddHours(-1);
                break;
            case "6h":
                query.From = now.AddHours(-6);
                break;
            case "24h":
                query.From = now.AddHours(-24);
                break;
            case "7d":
                query.From = now.AddDays(-7);
                break;
            case "30d":
                query.From = now.AddDays(-30);
                break;
        }
    }
}

public sealed class GetLogsSummaryQueryHandler(
    ILogRepository repo
) : IRequestHandler<GetLogsSummaryQuery, Result<LogSummaryDto>>
{
    public async Task<Result<LogSummaryDto>> Handle(GetLogsSummaryQuery q, CancellationToken ct)
    {
        var query = new LogQuery
        {
            AgentId = q.AgentId is not null && Guid.TryParse(q.AgentId, out var aid) ? aid : null,
            SiteId = q.SiteId is not null && Guid.TryParse(q.SiteId, out var sid) ? sid : null,
            ClientId = q.ClientId is not null && Guid.TryParse(q.ClientId, out var cid) ? cid : null,
            Limit = Math.Clamp(q.Limit, 1, 500),
            Level = q.Level.HasValue ? (LogLevel)q.Level.Value : null,
            Type = q.Type.HasValue ? (LogType)q.Type.Value : null,
            Source = q.Source.HasValue ? (LogSource)q.Source.Value : null,
            PeriodPreset = q.Period,
            SearchText = q.Search
        };

        if (!string.IsNullOrWhiteSpace(q.Period))
        {
            var now = DateTime.UtcNow;
            switch (q.Period.ToLowerInvariant())
            {
                case "1h": query.From = now.AddHours(-1); break;
                case "6h": query.From = now.AddHours(-6); break;
                case "24h": query.From = now.AddHours(-24); break;
                case "7d": query.From = now.AddDays(-7); break;
                case "30d": query.From = now.AddDays(-30); break;
            }
        }

        var raw = await repo.GetSummaryAsync(query);
        var summary = new LogSummaryDto(
            raw.Total,
            q.Search,
            null, null, null, null,
            q.Period,
            query.From,
            query.To,
            raw.Levels,
            raw.Sources,
            raw.Types,
            raw.Clients.Select(c => new LogScopeFacetCountDto(c.Id, null, c.Count)).ToList().AsReadOnly(),
            raw.Sites.Select(s => new LogScopeFacetCountDto(s.Id, null, s.Count)).ToList().AsReadOnly(),
            raw.Agents.Select(a => new LogScopeFacetCountDto(a.Id, null, a.Count)).ToList().AsReadOnly());

        return Result<LogSummaryDto>.Success(summary);
    }
}

public sealed class GetLogsScopeOptionsQueryHandler(
    ILogRepository repo
) : IRequestHandler<GetLogsScopeOptionsQuery, Result<LogsScopeOptionsDto>>
{
    public async Task<Result<LogsScopeOptionsDto>> Handle(GetLogsScopeOptionsQuery q, CancellationToken ct)
    {
        // Buscar logs recentes (últimas 24h) para extrair escopos distintos
        var query = new LogQuery
        {
            Limit = 500,
            From = DateTime.UtcNow.AddHours(-24)
        };

        var raw = await repo.GetSummaryAsync(query);

        var levels = Enum.GetValues<LogLevel>()
            .Select(l => new LogEnumOptionDto((int)l, l.ToString()))
            .ToList().AsReadOnly();

        var types = Enum.GetValues<LogType>()
            .Select(t => new LogEnumOptionDto((int)t, t.ToString()))
            .ToList().AsReadOnly();

        var sources = Enum.GetValues<LogSource>()
            .Select(s => new LogEnumOptionDto((int)s, s.ToString()))
            .ToList().AsReadOnly();

        var clients = raw.Clients
            .Select(c => new LogScopeOptionDto(c.Id.ToString(), null))
            .ToList().AsReadOnly();

        var sites = raw.Sites
            .Select(s => new LogScopeOptionDto(s.Id.ToString(), null))
            .ToList().AsReadOnly();

        var agents = raw.Agents
            .Select(a => new LogScopeOptionDto(a.Id.ToString(), null))
            .ToList().AsReadOnly();

        var result = new LogsScopeOptionsDto(clients, sites, agents, levels, types, sources);
        return Result<LogsScopeOptionsDto>.Success(result);
    }
}

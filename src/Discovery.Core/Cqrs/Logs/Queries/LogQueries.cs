using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Logs.Queries;

public sealed record ListLogsQuery(
    string? AgentId = null, string? SiteId = null, string? ClientId = null,
    string? Cursor = null, int Limit = 50
) : IQuery<Result<LogsPageDto>>;

public sealed record LogDto(
    Guid Id, string Level, string Type, string Source,
    string Message, DateTime CreatedAt
);

public sealed record LogsPageDto(
    IReadOnlyList<LogDto> Items, string? NextCursor, bool HasMore
);

using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Alerts.Queries;

public sealed record ListAgentAlertsQuery(
    string? Status, string? ScopeType, Guid? ScopeClientId, Guid? ScopeSiteId,
    Guid? ScopeAgentId, Guid? TicketId, string? Cursor, int Limit = 100
) : IQuery<Result<ListAlertsResult>>;

public sealed record ListAlertsResult(
    IReadOnlyList<AlertDto> Items, string? NextCursor, bool HasMore, int Total
);

public sealed record AlertDto(
    Guid Id, Guid? AgentId, string Title, string Severity, string Status, DateTime CreatedAt
);

public sealed record GetAlertByIdQuery(Guid Id) : IQuery<Result<AlertDetailDto>>;

public sealed record AlertDetailDto(
    Guid Id, Guid? AgentId, Guid? SiteId, Guid? ClientId,
    string Title, string Description, string Severity, string Status,
    DateTime CreatedAt, DateTime? AcknowledgedAt, Guid? TicketId
);

using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Alerts.Queries;

public sealed record ListAgentAlertsQuery(
    string? Status, string? ScopeType, Guid? ScopeClientId, Guid? ScopeSiteId,
    Guid? ScopeAgentId, Guid? TicketId, string? Cursor, int Limit = 100,
    // Inclui alertas escopados ao cliente/site que também valem para a máquina
    // (ScopeAgentId nulo). Usado pela tool get_agent_alerts; o endpoint do
    // console mantém o comportamento antigo (false).
    bool IncludeParentScopes = false
) : IQuery<Result<ListAlertsResult>>;

public sealed record ListAlertsResult(
    IReadOnlyList<AlertDto> Items, string? NextCursor, bool HasMore, int Total
);

public sealed record AlertDto(
    Guid Id, Guid? AgentId, string Title, string Severity, string Status, DateTime CreatedAt,
    // Ticket vinculado (opcional). Adicionado como parâmetro opcional para não
    // quebrar consumidores existentes; a tool get_agent_alerts usa para
    // correlacionar alerta -> chamado sem uma segunda consulta por item.
    Guid? TicketId = null
);

public sealed record GetAlertByIdQuery(Guid Id) : IQuery<Result<AlertDetailDto>>;

public sealed record AlertDetailDto(
    Guid Id, Guid? AgentId, Guid? SiteId, Guid? ClientId,
    string Title, string Description, string Severity, string Status,
    DateTime CreatedAt, DateTime? AcknowledgedAt, Guid? TicketId
);

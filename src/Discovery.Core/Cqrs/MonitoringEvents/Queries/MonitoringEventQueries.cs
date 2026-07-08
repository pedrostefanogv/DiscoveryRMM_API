using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.MonitoringEvents.Queries;

public sealed record ListMonitoringEventsQuery(Guid? AgentId, Guid? ClientId, Guid? SiteId, string? Cursor = null, int Limit = 50) : IQuery<Result<IReadOnlyList<MonitoringEventDto>>>;

public sealed record MonitoringEventDto(Guid Id, Guid? AgentId, string AlertCode, string Severity, string Source, DateTime CreatedAt, string? Labels);

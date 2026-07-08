using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Tickets.Queries;

// ── Watchers ─────────────────────────────────────────────────────────────
public sealed record GetTicketWatchersQuery(Guid TicketId) : IQuery<Result<IEnumerable<TicketWatcher>>>;

// ── Remote Sessions ──────────────────────────────────────────────────────
public sealed record GetTicketRemoteSessionsQuery(Guid TicketId) : IQuery<Result<IEnumerable<TicketRemoteSession>>>;

// ── Automation Links ─────────────────────────────────────────────────────
public sealed record GetTicketAutomationLinksQuery(Guid TicketId) : IQuery<Result<IReadOnlyList<TicketAutomationLink>>>;

// ── Knowledge Links ──────────────────────────────────────────────────────
public sealed record GetTicketKnowledgeLinksQuery(Guid TicketId) : IQuery<Result<List<TicketKnowledgeLink>>>;

// ── Audit Timeline ───────────────────────────────────────────────────────
public sealed record GetTicketAuditTimelineQuery(Guid TicketId) : IQuery<Result<List<TicketActivityLog>>>;

// ── KPI ──────────────────────────────────────────────────────────────────
public sealed record GetTicketKpiQuery(Guid? ClientId, Guid? DepartmentId, DateTime? Since) : IQuery<Result<TicketKpiResult>>;

using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Tickets.Commands;

// ── Watchers ─────────────────────────────────────────────────────────────
public sealed record AddTicketWatcherCommand(Guid TicketId, Guid UserId, string? AddedBy) : ICommand<Result<TicketWatcher>>;
public sealed record RemoveTicketWatcherCommand(Guid TicketId, Guid UserId) : ICommand<Result<VoidResult>>;

// ── Remote Sessions ──────────────────────────────────────────────────────
public sealed record CreateTicketRemoteSessionCommand(Guid TicketId, Guid? AgentId, string? MeshNodeId, string? StartedBy, string? Note) : ICommand<Result<TicketRemoteSession>>;
public sealed record EndTicketRemoteSessionCommand(Guid TicketId, Guid SessionId) : ICommand<Result<TicketRemoteSession>>;

// ── Automation Links ─────────────────────────────────────────────────────
public sealed record CreateTicketAutomationLinkCommand(Guid TicketId, Guid AutomationTaskDefinitionId, string? RequestedBy, string? Note) : ICommand<Result<TicketAutomationLink>>;

// ── Knowledge Links ──────────────────────────────────────────────────────
public sealed record CreateTicketKnowledgeLinkCommand(Guid TicketId, Guid ArticleId, Guid? AddedByUserId, string? Note) : ICommand<Result<TicketKnowledgeLink>>;
public sealed record DeleteTicketKnowledgeLinkCommand(Guid LinkId) : ICommand<Result<VoidResult>>;

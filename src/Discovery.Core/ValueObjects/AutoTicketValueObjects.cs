using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.ValueObjects;

public class AutoTicketRuleDecision
{
    public AutoTicketRule? Rule { get; init; }
    public AutoTicketDecision Decision { get; init; } = AutoTicketDecision.MatchedNoAction;
    public string Reason { get; init; } = string.Empty;
    public bool Matched => Rule is not null;
    public bool ShouldCreateTicket => Rule?.Action == AutoTicketRuleAction.CreateTicket;
    public bool IsSuppressed => Rule?.Action == AutoTicketRuleAction.Suppress;
}

public class AutoTicketDedupResult
{
    public required string DedupKey { get; init; }
    public bool Acquired { get; init; }
    public Guid? ExistingTicketId { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

public class AutoTicketCreateTicketRequest
{
    public Guid ClientId { get; init; }
    public Guid? SiteId { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? WorkflowProfileId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? Category { get; init; }
    public TicketPriority Priority { get; init; } = TicketPriority.Medium;
    public string ActivityMessage { get; init; } = string.Empty;
}
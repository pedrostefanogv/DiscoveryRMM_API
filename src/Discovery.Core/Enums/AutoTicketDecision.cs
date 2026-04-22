namespace Discovery.Core.Enums;

public enum AutoTicketDecision
{
    MatchedNoAction = 0,
    Suppressed = 1,
    Deduped = 2,
    Created = 3,
    Failed = 4,
    RateLimited = 5
}
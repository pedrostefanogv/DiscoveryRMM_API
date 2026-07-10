namespace Discovery.Core.DTOs;

// ── Ticket AI Response DTOs ───────────────────────────────────────────

public record TicketAiBaseResult(string Content, int TokensUsed, string? Model);
public record TicketAiTriageResult(string Suggestion, int TokensUsed, string? Model);
public record TicketAiSummaryResult(string Summary, int TokensUsed, string? Model);
public record TicketAiSuggestedReplyResult(string SuggestedReply, int TokensUsed, string? Model);
public record TicketAiDraftKbResult(string Content, int TokensUsed, string? Model);

using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.TicketAi.Commands;

public sealed record TicketAiTriageCommand(Guid TicketId) : ICommand<Result<TicketAiTriageResult>>;
public sealed record TicketAiSummarizeCommand(Guid TicketId) : ICommand<Result<TicketAiSummaryResult>>;
public sealed record TicketAiSuggestReplyCommand(Guid TicketId) : ICommand<Result<TicketAiSuggestedReplyResult>>;
public sealed record TicketAiDraftKbArticleCommand(Guid TicketId, bool Persist = false) : ICommand<Result<TicketAiDraftKbResult>>;

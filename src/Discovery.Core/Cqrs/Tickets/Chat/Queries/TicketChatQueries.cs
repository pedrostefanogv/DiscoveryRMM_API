using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Chat.Dtos;

namespace Discovery.Core.Cqrs.Tickets.Chat.Queries;

/// <summary>Sessão pendente/ativa do chamado (HasSession=false quando não há).</summary>
public sealed record GetTicketChatStateQuery(Guid TicketId)
    : IQuery<Result<TicketChatStateDto>>;

/// <summary>Snapshot incremental das mensagens (carregamento inicial e reconciliação).</summary>
public sealed record GetTicketChatMessagesQuery(Guid SessionId, long AfterSequence, int Limit)
    : IQuery<Result<IReadOnlyList<TicketChatMessageDto>>>;

/// <summary>Conversas anteriores do chamado (somente leitura no console web).</summary>
public sealed record GetTicketChatHistoryQuery(Guid TicketId, int Skip, int Take)
    : IQuery<Result<IReadOnlyList<TicketChatSessionDto>>>;

/// <summary>Sessão pendente/ativa do agent (usado no startup do agent).</summary>
public sealed record GetAgentActiveChatQuery(Guid AgentId)
    : IQuery<Result<TicketChatStateDto>>;

/// <summary>Sessão por id (usado pela ingestão de envelopes NATS).</summary>
public sealed record GetTicketChatSessionQuery(Guid SessionId)
    : IQuery<Result<TicketChatStateDto>>;

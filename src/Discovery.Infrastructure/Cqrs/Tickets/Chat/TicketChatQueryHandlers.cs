using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Chat.Dtos;
using Discovery.Core.Cqrs.Tickets.Chat.Queries;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Cqrs.Tickets.Chat;

/// <summary>Sessão pendente/ativa do chamado.</summary>
public sealed class GetTicketChatStateQueryHandler(ITicketChatRepository chatRepository)
    : IRequestHandler<GetTicketChatStateQuery, Result<TicketChatStateDto>>
{
    public async Task<Result<TicketChatStateDto>> Handle(GetTicketChatStateQuery query, CancellationToken ct)
    {
        var session = await chatRepository.GetCurrentForTicketAsync(query.TicketId, ct);
        return Result<TicketChatStateDto>.Success(session is null
            ? new TicketChatStateDto(false, null)
            : new TicketChatStateDto(true, TicketChatMapper.MapSession(session)));
    }
}

/// <summary>Snapshot incremental das mensagens.</summary>
public sealed class GetTicketChatMessagesQueryHandler(
    ITicketChatRepository chatRepository)
    : IRequestHandler<GetTicketChatMessagesQuery, Result<IReadOnlyList<TicketChatMessageDto>>>
{
    public async Task<Result<IReadOnlyList<TicketChatMessageDto>>> Handle(GetTicketChatMessagesQuery query, CancellationToken ct)
    {
        var limit = query.Limit <= 0 ? 200 : Math.Min(query.Limit, 500);
        var messages = await chatRepository.GetMessagesAsync(query.SessionId, query.AfterSequence, limit, ct);
        if (messages.Count == 0)
            return Result<IReadOnlyList<TicketChatMessageDto>>.Success([]);

        var attachments = await chatRepository.GetAttachmentsForMessagesAsync(messages.Select(m => m.Id).ToArray(), ct);
        return Result<IReadOnlyList<TicketChatMessageDto>>.Success(TicketChatMapper.MapMessages(messages, attachments));
    }
}

/// <summary>Conversas anteriores do chamado (somente leitura).</summary>
public sealed class GetTicketChatHistoryQueryHandler(
    ITicketChatRepository chatRepository,
    IOptions<TicketChatOptions> options)
    : IRequestHandler<GetTicketChatHistoryQuery, Result<IReadOnlyList<TicketChatSessionDto>>>
{
    public async Task<Result<IReadOnlyList<TicketChatSessionDto>>> Handle(GetTicketChatHistoryQuery query, CancellationToken ct)
    {
        var take = query.Take <= 0 ? 20 : Math.Min(query.Take, Math.Max(1, options.Value.MaxHistoryPageSize));
        var sessions = await chatRepository.GetHistoryAsync(query.TicketId, query.Skip, take, ct);

        IReadOnlyList<TicketChatSessionDto> result = sessions
            .Select(s => TicketChatMapper.MapSession(s))
            .ToList();

        return Result<IReadOnlyList<TicketChatSessionDto>>.Success(result);
    }
}

/// <summary>Sessão pendente/ativa do agent.</summary>
public sealed class GetAgentActiveChatQueryHandler(ITicketChatRepository chatRepository)
    : IRequestHandler<GetAgentActiveChatQuery, Result<TicketChatStateDto>>
{
    public async Task<Result<TicketChatStateDto>> Handle(GetAgentActiveChatQuery query, CancellationToken ct)
    {
        var session = await chatRepository.GetCurrentForAgentAsync(query.AgentId, ct);
        return Result<TicketChatStateDto>.Success(session is null
            ? new TicketChatStateDto(false, null)
            : new TicketChatStateDto(true, TicketChatMapper.MapSession(session)));
    }
}

/// <summary>Sessão por id.</summary>
public sealed class GetTicketChatSessionQueryHandler(ITicketChatRepository chatRepository)
    : IRequestHandler<GetTicketChatSessionQuery, Result<TicketChatStateDto>>
{
    public async Task<Result<TicketChatStateDto>> Handle(GetTicketChatSessionQuery query, CancellationToken ct)
    {
        var session = await chatRepository.GetByIdAsync(query.SessionId, ct);
        return Result<TicketChatStateDto>.Success(session is null
            ? new TicketChatStateDto(false, null)
            : new TicketChatStateDto(true, TicketChatMapper.MapSession(session)));
    }
}

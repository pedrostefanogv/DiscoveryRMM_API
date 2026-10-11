using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Chat.Dtos;

namespace Discovery.Core.Cqrs.Tickets.Chat.Commands;

/// <summary>
/// Inicia uma sessão de chat de suporte para o chamado. Somente o atendente
/// (endpoint user-auth) pode criar; valida agent presente/online, ausência de
/// sessão pendente/ativa no chamado e de chat ativo na mesma máquina.
/// </summary>
public sealed record StartTicketChatSessionCommand(
    Guid TicketId,
    Guid? StartedByUserId,
    string? StartedByName
) : ICommand<Result<TicketChatSessionDto>>;

/// <summary>Reenvia o convite de uma sessão pendente ainda não expirada.</summary>
public sealed record RetryTicketChatInviteCommand(Guid SessionId)
    : ICommand<Result<TicketChatSessionDto>>;

/// <summary>
/// Resposta do agent ao convite (aceite/recusa). Somente agent-auth; a sessão
/// precisa pertencer ao agent autenticado.
/// </summary>
public sealed record RespondTicketChatInviteCommand(
    Guid SessionId,
    Guid AgentId,
    bool Accepted,
    string? Reason
) : ICommand<Result<TicketChatSessionDto>>;

/// <summary>
/// Envia uma mensagem na sessão. `ClientMessageId` é a chave de idempotência
/// (gerada pelo servidor quando ausente).
/// </summary>
public sealed record SendTicketChatMessageCommand(
    Guid SessionId,
    Guid? AuthorUserId,
    string? AuthorName,
    string AuthorKind,
    string Body,
    IReadOnlyList<Guid>? AttachmentIds,
    Guid? ClientMessageId
) : ICommand<Result<TicketChatMessageDto>>;

/// <summary>
/// Encerra a sessão (suporte, usuário final via agent, ou sistema/job).
/// </summary>
public sealed record EndTicketChatSessionCommand(
    Guid SessionId,
    string EndedBy,
    string? Reason
) : ICommand<Result<TicketChatSessionDto>>;

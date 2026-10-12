namespace Discovery.Core.Interfaces;

/// <summary>
/// Presenca de "estou olhando esta conversa" do chat de suporte.
///
/// Enquanto o atendente mantem a janela do chat aberta e em foco, o console
/// renova a presenca por heartbeat; quando ele fecha a janela, oculta a aba ou
/// tira o foco, a presenca e liberada (ou expira sozinha pelo TTL). O envio de
/// mensagem pelo usuario final usa isso para NAO notificar (sino + Web Push)
/// uma mensagem que o atendente esta lendo na hora.
///
/// `clientId` identifica UMA instancia de janela/efeito: um DELETE atrasado de
/// uma montagem antiga nao pode derrubar a presenca valida de outra janela
/// (React StrictMode monta/desmonta/monta em dev, e abrir o chat em duas
/// janelas e possivel). Sem clientId, Clear remove incondicionalmente.
/// </summary>
public interface ITicketChatPresenceService
{
    /// <summary>Tempo de vida da presenca; o cliente renova antes de expirar.</summary>
    int TtlSeconds { get; }

    /// <summary>Marca/renova que o usuario esta vendo a conversa do chamado.</summary>
    Task TouchAsync(Guid userId, Guid ticketId, string? clientId = null, CancellationToken cancellationToken = default);

    /// <summary>Remove a presenca (janela fechada, aba oculta ou desmontagem).</summary>
    Task ClearAsync(Guid userId, Guid ticketId, string? clientId = null, CancellationToken cancellationToken = default);

    /// <summary>True quando a presenca do usuario para o chamado ainda esta valida.</summary>
    Task<bool> IsViewingAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default);

    /// <summary>Marca/renova que o usuario esta vendo uma conversa AVULSA (sem chamado).</summary>
    Task TouchSessionAsync(Guid userId, Guid sessionId, string? clientId = null, CancellationToken cancellationToken = default);

    /// <summary>Remove a presenca da conversa avulsa.</summary>
    Task ClearSessionAsync(Guid userId, Guid sessionId, string? clientId = null, CancellationToken cancellationToken = default);

    /// <summary>True quando a presenca do usuario para a conversa avulsa ainda esta valida.</summary>
    Task<bool> IsViewingSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);
}
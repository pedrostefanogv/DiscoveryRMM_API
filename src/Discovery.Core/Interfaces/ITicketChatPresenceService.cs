namespace Discovery.Core.Interfaces;

/// <summary>
/// Presenca de "estou olhando esta conversa" do chat de suporte.
///
/// Enquanto o atendente mantem a janela do chat aberta e visivel, o console
/// renova a presenca por heartbeat; quando ele fecha a janela/aba ou troca de
/// aplicacao, a presenca expira sozinha. O envio de mensagem pelo usuario final
/// usa isso para NAO notificar (sino + Web Push) uma mensagem que o atendente
/// esta lendo na hora — antes, toda mensagem virava notificacao mesmo com a
/// conversa aberta na frente dele.
/// </summary>
public interface ITicketChatPresenceService
{
    /// <summary>Tempo de vida da presenca; o cliente renova antes de expirar.</summary>
    int TtlSeconds { get; }

    /// <summary>Marca/renova que o usuario esta vendo a conversa do chamado.</summary>
    Task TouchAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default);

    /// <summary>Remove a presenca (janela fechada, aba oculta ou desmontagem).</summary>
    Task ClearAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default);

    /// <summary>True quando a presenca do usuario para o chamado ainda esta valida.</summary>
    Task<bool> IsViewingAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default);
}

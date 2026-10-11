namespace Discovery.Core.Enums;

/// <summary>
/// Autor de uma mensagem do chat de suporte.
/// </summary>
public enum TicketChatAuthorKind
{
    /// <summary>Atendente de suporte (console web).</summary>
    Support = 0,

    /// <summary>Usuário final da máquina (janela do agent ou companion).</summary>
    EndUser = 1,

    /// <summary>Mensagem de sistema (abertura, encerramento, expiração).</summary>
    System = 2
}

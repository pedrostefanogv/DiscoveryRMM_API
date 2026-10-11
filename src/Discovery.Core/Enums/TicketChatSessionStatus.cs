namespace Discovery.Core.Enums;

/// <summary>
/// Ciclo de vida de uma sessão de chat de suporte vinculada a um chamado.
/// </summary>
public enum TicketChatSessionStatus
{
    /// <summary>Convite enviado ao agent; aguardando confirmação do usuário.</summary>
    Pending = 0,

    /// <summary>Usuário confirmou; a janela de chat está aberta na máquina.</summary>
    Active = 1,

    /// <summary>Usuário recusou, ou o agent estava ocupado/offline.</summary>
    Declined = 2,

    /// <summary>Convite venceu sem resposta dentro de TicketChat:InviteTimeoutSeconds.</summary>
    Expired = 3,

    /// <summary>Encerrada pelo usuário, pelo suporte ou pelo sistema.</summary>
    Ended = 4
}

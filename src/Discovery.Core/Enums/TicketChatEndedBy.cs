namespace Discovery.Core.Enums;

/// <summary>
/// Quem encerrou a sessão de chat de suporte.
/// </summary>
public enum TicketChatEndedBy
{
    /// <summary>Usuário final fechou a janela do chat.</summary>
    User = 0,

    /// <summary>Atendente de suporte encerrou pelo console.</summary>
    Support = 1,

    /// <summary>Job de manutenção (inatividade, chamado fechado, etc.).</summary>
    System = 2
}

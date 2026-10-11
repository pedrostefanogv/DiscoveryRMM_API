namespace Discovery.Core.Helpers;

/// <summary>
/// Textos de auditoria do chat de suporte gravados no log de atividades do
/// chamado (`TicketActivityLog.Comment`). Ficam aqui para que o console, os
/// handlers e o job de manutenção falem exatamente a mesma língua na timeline.
/// </summary>
public static class TicketChatAuditText
{
    public static string Started(string? supportName)
        => string.IsNullOrWhiteSpace(supportName)
            ? "Chat de suporte iniciado · aguardando confirmação na máquina"
            : $"Chat de suporte iniciado por {supportName.Trim()} · aguardando confirmação na máquina";

    public static string Ended(string? reason) => (reason ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "support_closed" => "Chat de suporte encerrado pelo suporte",
        "user_closed" => "Chat de suporte encerrado pelo usuário",
        "inactivity" => "Chat de suporte encerrado por inatividade",
        "ticket_closed" => "Chat de suporte encerrado pelo fechamento do chamado",
        _ => "Chat de suporte encerrado"
    };

    /// <summary>Terminais "sem conversa": recusa, expiração do convite e indisponibilidade.</summary>
    public static string Declined(string? reason) => (reason ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "timeout" => "Convite de chat expirado sem confirmação na máquina",
        "busy" => "Convite de chat recusado: a máquina já estava em outro chat",
        "offline" => "Convite de chat não entregue: máquina offline",
        "no_ui" => "Convite de chat não entregue: agent sem interface para confirmar",
        "confirmation_disabled" => "Convite de chat recusado: confirmação obrigatória desabilitada por política",
        "notifications_disabled" => "Convite de chat não entregue: notificações bloqueadas por política",
        "user_denied" => "Chat de suporte recusado pelo usuário na máquina",
        _ => "Chat de suporte recusado na máquina"
    };
}

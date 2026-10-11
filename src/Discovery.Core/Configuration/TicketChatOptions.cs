namespace Discovery.Core.Configuration;

/// <summary>
/// Configuração do chat de suporte vinculado ao chamado (seção `TicketChat`).
/// Os tipos/tamanhos de anexo continuam governados por TicketAttachmentSettings.
/// </summary>
public class TicketChatOptions
{
    public const string SectionName = "TicketChat";

    /// <summary>Prazo do convite antes de o job marcar a sessão como Expired.</summary>
    public int InviteTimeoutSeconds { get; set; } = 120;

    /// <summary>Sessão ativa sem mensagens por este tempo é encerrada pelo sistema.</summary>
    public int InactivityTimeoutMinutes { get; set; } = 30;

    /// <summary>Tamanho máximo do corpo de uma mensagem.</summary>
    public int MaxMessageLength { get; set; } = 8192;

    /// <summary>Máximo de anexos por mensagem.</summary>
    public int MaxAttachmentsPerMessage { get; set; } = 10;

    /// <summary>Máximo de sessões devolvidas por página do histórico.</summary>
    public int MaxHistoryPageSize { get; set; } = 100;

    /// <summary>Intervalo mínimo entre reenvios do convite da mesma sessão.</summary>
    public int InviteRetryCooldownSeconds { get; set; } = 30;

    /// <summary>Notifica o atendente que iniciou a sessão quando o usuário responde.</summary>
    public bool NotifySupportOnMessage { get; set; } = true;
}

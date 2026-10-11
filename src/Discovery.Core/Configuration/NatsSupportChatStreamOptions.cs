using Discovery.Core.Helpers;

namespace Discovery.Core.Configuration;

/// <summary>
/// Configuração do stream JetStream usado pelo chat de suporte vinculado ao
/// chamado (`DISCOVERY_SUPPORT_CHAT`).
///
/// Diferente do stream de fan-out de comandos (efêmero/replay de comando), aqui
/// a durabilidade é requisito funcional: uma mensagem do atendente enviada
/// enquanto o agent está reconectando NÃO pode se perder — o consumer durável
/// do agent faz replay a partir do último ack.
/// </summary>
public class NatsSupportChatStreamOptions
{
    public const string SectionName = "Nats:SupportChatStream";

    public bool Enabled { get; set; } = true;

    public string Name { get; set; } = "DISCOVERY_SUPPORT_CHAT";

    /// <summary>
    /// Um único wildcard cobrindo as duas direções do chat. Variantes com
    /// overlap são rejeitadas pelo NATS ("subject overlaps with").
    /// </summary>
    public string[] Subjects { get; set; } =
    [
        NatsSubjectBuilder.SupportChatStreamSubject,
    ];

    /// <summary>
    /// Janela de replay. Passado esse prazo o agent reconcilia pelo snapshot
    /// REST ao abrir a janela do chat.
    /// </summary>
    public string MaxAge { get; set; } = "24h";

    public long MaxBytes { get; set; } = 268_435_456;

    /// <summary>Janela de deduplicação de publish no broker JetStream.</summary>
    public string DuplicateWindow { get; set; } = "2m";

    public int RetryDelaySeconds { get; set; } = 10;

    /// <summary>0 = sem limite de tentativas (retry contínuo enquanto a API estiver ativa).</summary>
    public int MaxRetryAttempts { get; set; }
}

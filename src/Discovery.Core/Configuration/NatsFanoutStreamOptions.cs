using Discovery.Core.Helpers;

namespace Discovery.Core.Configuration;

/// <summary>
/// Configuracao do stream JetStream usado para replay de comandos fan-out.
/// </summary>
public class NatsFanoutStreamOptions
{
    public const string SectionName = "Nats:FanoutStream";

    public bool Enabled { get; set; } = true;

    public string Name { get; set; } = "DISCOVERY_FANOUT_COMMANDS";

    public string[] Subjects { get; set; } =
    [
        NatsSubjectBuilder.SiteAgentsCommandStreamSubject,
        NatsSubjectBuilder.ClientAgentsCommandStreamSubject,
        NatsSubjectBuilder.GlobalAgentsCommandStreamSubject,
    ];

    // Aceita formato compacto (ex.: 24h, 2m) e TimeSpan padrao (ex.: 24:00:00).
    public string MaxAge { get; set; } = "24h";

    public long MaxBytes { get; set; } = 134_217_728;

    // Janela de deduplicacao de publish no broker JetStream.
    public string DuplicateWindow { get; set; } = "2m";

    public int RetryDelaySeconds { get; set; } = 10;

    // 0 = sem limite de tentativas (retry continuo enquanto a API estiver ativa).
    public int MaxRetryAttempts { get; set; }
}

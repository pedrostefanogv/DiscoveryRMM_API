namespace Discovery.Core.Configuration;

/// <summary>
/// Reentrega de comandos não confirmados.
///
/// Contexto: o dispatch por agente usa NATS core (sem persistência). Se o agente
/// está offline no momento do envio, a mensagem não tem assinante e é perdida —
/// a linha ficava "Pending/Sent" para sempre e nada reexecutava o comando. Este
/// serviço reenvia periodicamente os comandos ainda não confirmados, para
/// agentes que estão online AGORA. O agente deduplica por CommandId, então um
/// comando nunca executa duas vezes por causa da reentrega.
/// </summary>
public class NatsCommandRedeliveryOptions
{
    public const string SectionName = "Nats:CommandRedelivery";

    public bool Enabled { get; set; } = true;

    /// <summary>Intervalo entre varreduras.</summary>
    public int IntervalSeconds { get; set; } = 120;

    /// <summary>Idade mínima de um comando nunca enviado para entrar na reentrega.</summary>
    public int MinAgeSeconds { get; set; } = 120;

    /// <summary>Intervalo mínimo entre tentativas para o mesmo comando.</summary>
    public int RetryGraceSeconds { get; set; } = 300;

    /// <summary>Comandos mais antigos que isso são abandonados (não reenviados).</summary>
    public int RetentionHours { get; set; } = 24;

    /// <summary>Comandos por varredura.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// Normaliza as janelas de varredura. Puro (sem I/O) para ser testável:
    /// valores fora da faixa não devem gerar um loop apertado nem uma janela
    /// infinita.
    /// </summary>
    public NatsCommandRedeliveryWindow ResolveWindow(DateTime nowUtc)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(IntervalSeconds, 10, 3600));
        var minAge = TimeSpan.FromSeconds(Math.Clamp(MinAgeSeconds, 0, 3600));
        var retryGrace = TimeSpan.FromSeconds(Math.Clamp(RetryGraceSeconds, 30, 3600));
        var retention = TimeSpan.FromHours(Math.Clamp(RetentionHours, 1, 168));
        var grace = minAge > retryGrace ? minAge : retryGrace;

        return new NatsCommandRedeliveryWindow(
            Interval: interval,
            CreatedAfterUtc: nowUtc - retention,
            StaleBeforeUtc: nowUtc - grace,
            BatchSize: Math.Clamp(BatchSize, 1, 1000));
    }
}

/// <summary>Janelas efetivas de uma varredura de reentrega.</summary>
public readonly record struct NatsCommandRedeliveryWindow(
    TimeSpan Interval,
    DateTime CreatedAfterUtc,
    DateTime StaleBeforeUtc,
    int BatchSize);

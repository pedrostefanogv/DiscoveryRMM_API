namespace Discovery.Core.Configuration;

/// <summary>
/// Configuracao da sinalizacao de sobrecarga no pong global do servidor.
/// </summary>
public class NatsGlobalPongOptions
{
    public const string SectionName = "Nats:GlobalPong";

    // Valores aceitos: disabled | forced | auto
    public string OverloadMode { get; set; } = "auto";

    // Intervalo fixo de publicacao do pong global, independente de heartbeats.
    public int PublishIntervalSeconds { get; set; } = 60;

    // Usado no modo forced. null = omite serverOverloaded no payload.
    public bool? ForcedValue { get; set; }

    // Usado no modo auto: percentual de uso de worker threads para marcar sobrecarga.
    public double WorkerThreadUsageThresholdPercent { get; set; } = 90;

    // Usado no modo auto: percentual de memoria gerenciada para marcar sobrecarga.
    public double ManagedMemoryUsageThresholdPercent { get; set; } = 85;
}

namespace Discovery.Core.Configuration;

/// <summary>
/// Opções de configuração do sistema P2P (descoberta, distribuição).
/// Lidas da seção "P2p" do appsettings.json.
/// </summary>
public class P2pOptions
{
    public const string SectionName = "P2p";

    /// <summary>
    /// Habilita o subject de descoberta por site via NATS.
    /// </summary>
    public bool UseSiteSubject { get; set; } = true;

    /// <summary>
    /// Janela de debounce em ms para coalescer atualizações de descoberta.
    /// </summary>
    public int PublishDebounceMs { get; set; } = 1500;

    /// <summary>
    /// TTL do snapshot em segundos (orienta cache no agent).
    /// </summary>
    public int TtlSeconds { get; set; } = 120;

    /// <summary>
    /// Número máximo de peers por snapshot (ordenado por lastHeartbeatAt).
    /// </summary>
    public int MaxPeersPerSnapshot { get; set; } = 500;

    /// <summary>
    /// Pula publish se o snapshot não mudou em relação ao anterior (hash comparado).
    /// </summary>
    public bool SkipPublishIfUnchanged { get; set; } = true;
}

namespace Discovery.Core.Configuration;

/// <summary>
/// Opções de configuração do sistema P2P (distribuição, lock).
/// Lidas da seção "P2p" do appsettings.json.
/// </summary>
public class P2pOptions
{
    public const string SectionName = "P2p";

    /// <summary>
    /// TTL do snapshot em segundos (orienta cache no agent).
    /// </summary>
    public int TtlSeconds { get; set; } = 120;

    /// <summary>
    /// Número máximo de peers por snapshot (ordenado por lastHeartbeatAt).
    /// </summary>
    public int MaxPeersPerSnapshot { get; set; } = 500;
}

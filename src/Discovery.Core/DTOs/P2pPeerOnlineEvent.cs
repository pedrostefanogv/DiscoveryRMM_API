namespace Discovery.Core.DTOs;

/// <summary>
/// Evento publicado no subject tenant.{clientId}.p2p.events
/// quando um agent transiciona Offline → Online e possui PeerId configurado.
/// </summary>
public class P2pPeerOnlineEvent
{
    public string EventType { get; set; } = "peer.online";
    public Guid ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid AgentId { get; set; }
    public string PeerId { get; set; } = string.Empty;
    public IReadOnlyList<string> Addrs { get; set; } = Array.Empty<string>();
    public int Port { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
}

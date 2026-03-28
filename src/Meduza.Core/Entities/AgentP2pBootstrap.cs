namespace Meduza.Core.Entities;

/// <summary>
/// Registro de presença de um agente no sistema de bootstrap P2P via cloud.
/// Atualizado a cada chamada ao endpoint POST /api/agent-auth/me/p2p/bootstrap.
/// Permite que os agents descubram peers (outros agents do mesmo cliente) para
/// estabelecer conexões libp2p diretas sem depender de broadcast LAN.
/// </summary>
public class AgentP2pBootstrap
{
    /// <summary>ID do agent (PK e FK para agents.id)</summary>
    public Guid AgentId { get; set; }

    /// <summary>ID do cliente ao qual este agent pertence (via site)</summary>
    public Guid ClientId { get; set; }

    /// <summary>Peer ID libp2p do agent (formato 12D3KooW...)</summary>
    public string PeerId { get; set; } = string.Empty;

    /// <summary>IPs IPv4 roteáveis do host, serializado como JSON array (ex: ["192.168.1.50","10.50.0.12"])</summary>
    public string AddrsJson { get; set; } = "[]";

    /// <summary>Porta TCP/QUIC libp2p do agent (range típico: 41080–41120)</summary>
    public int Port { get; set; }

    /// <summary>Timestamp do último bootstrap/heartbeat recebido (UTC)</summary>
    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;
}

namespace Meduza.Core.DTOs;

/// <summary>
/// Request body do endpoint POST /api/agent-auth/me/p2p/bootstrap.
/// O agent envia seu peer ID libp2p, IPs roteáveis e porta para ser registrado no servidor
/// e receber uma lista de peers ativos do mesmo cliente.
/// </summary>
public record P2pBootstrapRequest(
    /// <summary>ID do agente (igual ao campo agentId do config.json)</summary>
    string AgentId,

    /// <summary>Peer ID libp2p (formato 12D3KooW...)</summary>
    string PeerId,

    /// <summary>Lista de IPs IPv4 roteáveis do host (sem porta)</summary>
    IReadOnlyList<string> Addrs,

    /// <summary>Porta TCP/QUIC libp2p do agent (range típico: 41080–41120)</summary>
    int Port
);

/// <summary>
/// Response do endpoint POST /api/agent-auth/me/p2p/bootstrap.
/// Retorna até 3 peers online do mesmo cliente para o agent estabelecer conexões libp2p.
/// </summary>
public record P2pBootstrapResponse(
    /// <summary>Lista de até 3 peers online (vazia se nenhum outro agent está registrado/online)</summary>
    IReadOnlyList<P2pBootstrapPeerDto> Peers
);

/// <summary>
/// Informações de um peer para o bootstrap P2P.
/// </summary>
public record P2pBootstrapPeerDto(
    /// <summary>Peer ID libp2p do agent remoto</summary>
    string PeerId,

    /// <summary>IPs IPv4 conhecidos do agent remoto</summary>
    IReadOnlyList<string> Addrs,

    /// <summary>Porta TCP/QUIC libp2p do agent remoto</summary>
    int Port
);

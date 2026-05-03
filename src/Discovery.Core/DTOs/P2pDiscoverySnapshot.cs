namespace Discovery.Core.DTOs;

/// <summary>
/// Snapshot de peers P2P publicado pelo servidor no subject
/// tenant.{clientId}.site.{siteId}.p2p.discovery.
/// Apenas o servidor publica; agents assinam o subject do próprio site.
/// </summary>
public record P2pDiscoverySnapshot(
    int Version,
    Guid ClientId,
    Guid SiteId,
    DateTime GeneratedAtUtc,
    int TtlSeconds,
    long Sequence,
    IReadOnlyList<P2pDiscoveryPeerDto> Peers
);

public record P2pDiscoveryPeerDto(
    Guid AgentId,
    string PeerId,
    IReadOnlyList<string> Addrs,
    int Port,
    DateTime LastHeartbeatAtUtc
);

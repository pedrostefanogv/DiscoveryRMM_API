using System.Text.Json.Serialization;

namespace Discovery.Core.DTOs;

/// <summary>
/// Request do agent para POST /agent-auth/me/p2p/bootstrap.
/// A identidade REAL vem do token (AgentId autenticado); os campos abaixo sao
/// informativos/auditoria. A porta informada deve ser a do LISTENER libp2p.
/// </summary>
public record P2pCloudBootstrapRequest(
    [property: JsonPropertyName("agentId")] string? AgentId,
    [property: JsonPropertyName("clientId")] string? ClientId,
    [property: JsonPropertyName("peerId")] string? PeerId,
    [property: JsonPropertyName("addrs")] string[]? Addrs,
    [property: JsonPropertyName("port")] int Port);

/// <summary>Peer retornado pelo bootstrap (enderecamento libp2p).</summary>
public record P2pCloudBootstrapPeerDto(
    [property: JsonPropertyName("peerId")] string PeerId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("addrs")] IReadOnlyList<string> Addrs,
    [property: JsonPropertyName("port")] int Port);

/// <summary>
/// Resposta do bootstrap. Disabled true indica que o toggle Bootstrap P2P via
/// Nuvem esta desligado (server/cliente); o agent trata como sucesso com zero
/// peers, sem backoff de erro.
/// </summary>
public record P2pCloudBootstrapResponseDto(
    [property: JsonPropertyName("peers")] IReadOnlyList<P2pCloudBootstrapPeerDto> Peers,
    [property: JsonPropertyName("disabled")] bool Disabled = false);

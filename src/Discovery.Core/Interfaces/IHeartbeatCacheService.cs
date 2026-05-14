using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Cache de heartbeat em Redis — fonte de verdade para status Online.
///
/// Estratégia:
/// - Redis é a fonte de verdade: chave existe = Online, chave expirou = Offline
/// - DB só é escrito quando há transição de status (Online↔Offline)
/// - TTL da chave = heartbeat_interval + grace_period (padrão 180s)
/// </summary>
public interface IHeartbeatCacheService
{
    /// <summary>
    /// Registra heartbeat no Redis. Só escreve no DB na transição Offline→Online.
    /// Retorna true se houve transição Offline→Online (chave não existia antes).
    /// </summary>
    Task<bool> SetHeartbeatAsync(AgentHeartbeat heartbeat, AgentStatus status, CancellationToken ct = default);

    /// <summary>Lê o estado atual do cache. Se não existir, o agente está offline.</summary>
    Task<HeartbeatCacheEntry?> GetHeartbeatAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Retorna todos os agentes com heartbeat ativo no Redis.</summary>
    Task<IReadOnlyList<HeartbeatCacheEntry>> GetActiveHeartbeatsAsync(CancellationToken ct = default);

    /// <summary>Remove heartbeat do cache. Persiste Offline no DB se houve transição Online→Offline.</summary>
    Task RemoveHeartbeatAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Verifica se o agente está online pelo Redis (chave existe e não expirou).</summary>
    Task<bool> IsOnlineAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// Detecta agentes cujo heartbeat expirou (estavam online mas não têm chave Redis).
    /// Compara a lista de agentIds contra o Redis.
    /// </summary>
    Task<IReadOnlyList<Guid>> DetectExpiredAsync(IReadOnlyList<Guid> knownOnlineAgentIds, CancellationToken ct = default);

    /// <summary>
    /// Registra o transporte ativo do agent (nats/nats_ws) para roteamento.
    /// </summary>
    Task SetTransportAsync(Guid agentId, string transport, CancellationToken ct = default);

    /// <summary>
    /// Retorna o transporte ativo do agent (nats/nats_ws/null).
    /// </summary>
    Task<string?> GetTransportAsync(Guid agentId, CancellationToken ct = default);
}

/// <summary>
/// Entrada de heartbeat no cache Redis com métricas opcionais de saúde do agent.
/// </summary>
public class HeartbeatCacheEntry
{
    public Guid AgentId { get; init; }
    public AgentStatus Status { get; init; }
    public string? IpAddress { get; init; }
    public string? Hostname { get; init; }
    public string? AgentVersion { get; init; }
    public DateTime LastHeartbeatAt { get; init; }

    // Métricas de saúde (opcionais)
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? MemoryTotalGb { get; init; }
    public double? MemoryUsedGb { get; init; }
    public double? DiskPercent { get; init; }
    public double? DiskTotalGb { get; init; }
    public double? DiskUsedGb { get; init; }
    public int? P2pPeers { get; init; }
    public long? UptimeSeconds { get; init; }
    public int? ProcessCount { get; init; }

    // ── NOVOS para descoberta P2P ──
    public string? PeerId { get; init; }
    public IReadOnlyList<string>? Addrs { get; init; }
    public int? Port { get; init; }
}

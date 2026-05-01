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
    /// <summary>Registra heartbeat no Redis. Só escreve no DB na transição Offline→Online.</summary>
    Task SetHeartbeatAsync(Guid agentId, AgentStatus status, string? ipAddress, CancellationToken ct = default);

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
    /// Registra o transporte ativo do agent (signalr/nats) para roteamento de fallback.
    /// Usado pelo AgentHub e NatsAgentMessaging para indicar qual canal está ativo.
    /// </summary>
    Task SetTransportAsync(Guid agentId, string transport, CancellationToken ct = default);

    /// <summary>
    /// Retorna o transporte ativo do agent (signalr/nats/null).
    /// Usado para decidir rota de fallback ao enviar comandos.
    /// </summary>
    Task<string?> GetTransportAsync(Guid agentId, CancellationToken ct = default);
}

/// <summary>
/// Entrada de heartbeat no cache Redis.
/// </summary>
public class HeartbeatCacheEntry
{
    public Guid AgentId { get; init; }
    public AgentStatus Status { get; init; }
    public string? IpAddress { get; init; }
    public DateTime LastHeartbeatAt { get; init; }
}

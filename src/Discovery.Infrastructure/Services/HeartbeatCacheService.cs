using System.Text.Json;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Cache de heartbeat em Redis — fonte de verdade para status Online.
/// 
/// Estratégia:
/// - Redis é a fonte de verdade: chave existe = Online, chave expirou = Offline
/// - DB só é escrito quando há transição de status (Online↔Offline)
/// - TTL da chave = heartbeat_interval + grace_period (padrão 180s)
/// - HeartbeatExpiryBackgroundService detecta expirações e escreve Offline no DB
///
/// Chave Redis: heartbeat:agent:{agentId:N} → JSON {agentId, ip, lastHeartbeatAt} (TTL 180s)
/// Conjunto Redis: heartbeat:online:agents → Set de agentIds online (para scan eficiente)
/// </summary>
public class HeartbeatCacheService : IHeartbeatCacheService
{
    private readonly IRedisService _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HeartbeatCacheService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string KeyPrefix = "heartbeat:agent:";
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(180);

    public HeartbeatCacheService(
        IRedisService redis,
        IServiceScopeFactory scopeFactory,
        ILogger<HeartbeatCacheService> logger)
    {
        _redis = redis;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private IAgentRepository GetAgentRepo()
    {
        var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IAgentRepository>();
    }

    /// <summary>
    /// Registra heartbeat no Redis. Só escreve no DB se o agente passou de Offline → Online.
    /// </summary>
    public async Task SetHeartbeatAsync(Guid agentId, AgentStatus status, string? ipAddress, CancellationToken ct = default)
    {
        if (!_redis.IsConnected)
        {
            // Fallback: escreve direto no DB quando Redis indisponível
            try { await GetAgentRepo().UpdateStatusAsync(agentId, status, ipAddress); }
            catch (Exception ex) { _logger.LogWarning(ex, "Heartbeat DB fallback failed for {AgentId}", agentId); }
            return;
        }

        try
        {
            var entry = new HeartbeatCacheEntry
            {
                AgentId = agentId,
                Status = status,
                IpAddress = ipAddress,
                LastHeartbeatAt = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(entry, JsonOptions);
            var key = $"{KeyPrefix}{agentId:N}";

            // Verifica se a chave já existe (se não, é uma transição Offline→Online)
            var existed = await _redis.GetAsync(key);
            await _redis.SetAsync(key, json, (int)DefaultTtl.TotalSeconds);

            if (string.IsNullOrWhiteSpace(existed))
            {
                // Transição Offline → Online: escreve no DB e adiciona ao set
                _logger.LogDebug("Agent {AgentId} transitioned Offline → Online — persisting to DB", agentId);
                try { await GetAgentRepo().UpdateStatusAsync(agentId, AgentStatus.Online, ipAddress); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist Online status for {AgentId}", agentId); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao cachear heartbeat {AgentId} no Redis", agentId);
        }
    }

    /// <summary>Verifica se o agente está online pelo Redis.</summary>
    public async Task<bool> IsOnlineAsync(Guid agentId, CancellationToken ct = default)
    {
        if (!_redis.IsConnected) return false;

        try
        {
            var json = await _redis.GetAsync($"{KeyPrefix}{agentId:N}");
            return !string.IsNullOrWhiteSpace(json);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Lê o estado atual do cache.</summary>
    public async Task<HeartbeatCacheEntry?> GetHeartbeatAsync(Guid agentId, CancellationToken ct = default)
    {
        if (!_redis.IsConnected) return null;

        try
        {
            var json = await _redis.GetAsync($"{KeyPrefix}{agentId:N}");
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<HeartbeatCacheEntry>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao ler heartbeat {AgentId} do Redis", agentId);
            return null;
        }
    }

    /// <summary>Retorna todos os agentes com heartbeat ativo no Redis.</summary>
    public async Task<IReadOnlyList<HeartbeatCacheEntry>> GetActiveHeartbeatsAsync(CancellationToken ct = default)
    {
        if (!_redis.IsConnected) return Array.Empty<HeartbeatCacheEntry>();

        var results = new List<HeartbeatCacheEntry>();
        try
        {
            var keys = await _redis.GetKeysByPrefixAsync(KeyPrefix, maxResults: 100000);
            foreach (var key in keys)
            {
                var json = await _redis.GetAsync(key);
                if (string.IsNullOrWhiteSpace(json)) continue;

                var entry = JsonSerializer.Deserialize<HeartbeatCacheEntry>(json, JsonOptions);
                if (entry is not null) results.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao listar heartbeats ativos do Redis");
        }
        return results;
    }

    /// <summary>
    /// Remove heartbeat do Redis. Se o agente estava Online, persiste Offline no DB (transição Online→Offline).
    /// </summary>
    public async Task RemoveHeartbeatAsync(Guid agentId, CancellationToken ct = default)
    {
        if (!_redis.IsConnected)
        {
            try { await GetAgentRepo().UpdateStatusAsync(agentId, AgentStatus.Offline, null); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist Offline status for {AgentId}", agentId); }
            return;
        }

        try
        {
            var key = $"{KeyPrefix}{agentId:N}";
            var existed = !string.IsNullOrWhiteSpace(await _redis.GetAsync(key));
            await _redis.DeleteAsync(key);

            if (existed)
            {
                // Transição Online → Offline: persiste no DB
                _logger.LogDebug("Agent {AgentId} transitioned Online → Offline — persisting to DB", agentId);
                try { await GetAgentRepo().UpdateStatusAsync(agentId, AgentStatus.Offline, null); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist Offline status for {AgentId}", agentId); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao remover heartbeat {AgentId} do Redis", agentId);
        }
    }

    /// <summary>
    /// Detecta agentes que expiraram (estavam online no DB mas não têm chave Redis).
    /// Compara agentes que o DB acredita estarem Online contra o Redis.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> DetectExpiredAsync(IReadOnlyList<Guid> knownOnlineAgentIds, CancellationToken ct = default)
    {
        if (!_redis.IsConnected) return Array.Empty<Guid>();

        var expired = new List<Guid>();
        foreach (var agentId in knownOnlineAgentIds)
        {
            var isOnline = await IsOnlineAsync(agentId, ct);
            if (!isOnline)
                expired.Add(agentId);
        }
        return expired;
    }

    // Obsoletos — mantidos para compatibilidade com IHeartbeatCacheService
    public Task MarkForSyncAsync(Guid agentId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Guid>> DrainSyncQueueAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
}

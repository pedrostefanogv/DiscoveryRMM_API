using System.Collections.Concurrent;
using System.Linq;

namespace Discovery.Infrastructure.Services.Remote;

/// <summary>
/// Armazena métricas agregadas de streaming por sessão (RTT, jitter, bandwidth, FPS).
/// Singleton — compartilhado entre AckFrameCommandHandler e AdaptiveQualityService.
/// </summary>
public class SessionMetricsStore
{
    private readonly ConcurrentDictionary<Guid, SessionMetricsSnapshot> _metrics = new();
    private readonly ConcurrentDictionary<Guid, bool> _autoModeSessions = new(); // sessões com modo Auto ativo

    /// <summary>
    /// Registra uma sessão para controle de qualidade adaptativa automática.
    /// </summary>
    public void EnableAutoMode(Guid sessionId) => _autoModeSessions[sessionId] = true;

    /// <summary>
    /// Remove uma sessão do modo adaptativo automático.
    /// </summary>
    public void DisableAutoMode(Guid sessionId) => _autoModeSessions.TryRemove(sessionId, out _);

    /// <summary>
    /// Verifica se uma sessão está em modo Auto.
    /// </summary>
    public bool IsAutoMode(Guid sessionId) => _autoModeSessions.ContainsKey(sessionId);

    /// <summary>
    /// Registra ou atualiza métricas de um frame (média móvel exponencial, evita overflow).
    /// </summary>
    public void RecordFrame(Guid sessionId, long frameSeq, double rttMs, double? jitterMs, double? estimatedBandwidthKbps)
    {
        const double alpha = 0.3; // fator de suavização EMA (0-1, menor = mais suave)
        _metrics.AddOrUpdate(sessionId,
            _ => new SessionMetricsSnapshot
            {
                SessionId = sessionId,
                EmaRttMs = rttMs,
                EmaJitterMs = jitterMs ?? 0,
                EmaBandwidthKbps = estimatedBandwidthKbps ?? 0,
                SampleCount = 1,
                LastFrameSeq = frameSeq,
                LastUpdate = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.EmaRttMs = existing.EmaRttMs * (1 - alpha) + rttMs * alpha;
                if (jitterMs.HasValue)
                    existing.EmaJitterMs = existing.EmaJitterMs * (1 - alpha) + jitterMs.Value * alpha;
                if (estimatedBandwidthKbps.HasValue)
                    existing.EmaBandwidthKbps = existing.EmaBandwidthKbps * (1 - alpha) + estimatedBandwidthKbps.Value * alpha;
                existing.SampleCount++;
                existing.LastFrameSeq = frameSeq;
                existing.LastUpdate = DateTime.UtcNow;
                return existing;
            });
    }

    /// <summary>
    /// Obtém as métricas agregadas para uma sessão.
    /// </summary>
    public SessionMetricsSnapshot? GetSnapshot(Guid sessionId)
    {
        return _metrics.TryGetValue(sessionId, out var snapshot) ? snapshot : null;
    }

    /// <summary>
    /// Remove métricas de uma sessão encerrada.
    /// </summary>
    public void Remove(Guid sessionId)
    {
        _metrics.TryRemove(sessionId, out _);
        _autoModeSessions.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Remove métricas stale (sem atualização há mais de N segundos).
    /// </summary>
    public int PurgeStale(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        int removed = 0;
        foreach (var kvp in _metrics)
        {
            if (kvp.Value.LastUpdate < cutoff && _metrics.TryRemove(kvp.Key, out _))
            {
                _autoModeSessions.TryRemove(kvp.Key, out _);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Retorna os IDs de todas as sessões em modo Auto com métricas suficientes.
    /// </summary>
    public IEnumerable<Guid> GetAutoModeSessions() => _autoModeSessions.Keys.ToArray();
}

public class SessionMetricsSnapshot
{
    public Guid SessionId { get; init; }

    // Médias móveis exponenciais (EMA) — não acumulam, evitam overflow
    public double EmaRttMs { get; set; }
    public double EmaJitterMs { get; set; }
    public double EmaBandwidthKbps { get; set; }

    public int SampleCount { get; set; }
    public long LastFrameSeq { get; set; }
    public DateTime LastUpdate { get; set; }
}

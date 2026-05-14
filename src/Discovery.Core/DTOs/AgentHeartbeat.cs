namespace Discovery.Core.DTOs;

/// <summary>
/// Heartbeat padronizado enviado pelo agent via NATS.
/// Todos os campos de métrica são opcionais — o servidor aceita heartbeats parciais.
/// </summary>
public record AgentHeartbeat(
    Guid AgentId,
    Guid? ClientId = null,
    Guid? SiteId = null,
    string? IpAddress = null,
    string? Hostname = null,
    string? AgentVersion = null,
    DateTime? TimestampUtc = null,
    double? CpuPercent = null,
    double? MemoryPercent = null,
    double? MemoryTotalGb = null,
    double? MemoryUsedGb = null,
    double? DiskPercent = null,
    double? DiskTotalGb = null,
    double? DiskUsedGb = null,
    int? P2pPeers = null,
    long? UptimeSeconds = null,
    int? ProcessCount = null,

    // ── NOVOS: dados de descoberta P2P ──
    string? PeerId = null,
    IReadOnlyList<string>? Addrs = null,
    int? Port = null
);

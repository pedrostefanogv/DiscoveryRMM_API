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
    double? CpuTemperatureCelsius = null,
    double? MemoryPercent = null,
    double? MemoryTotalGb = null,
    double? MemoryUsedGb = null,
    double? DiskPercent = null,
    double? DiskTotalGb = null,
    double? DiskUsedGb = null,
    double? DiskReadPercent = null,
    double? DiskWritePercent = null,
    double? DiskResponseMs = null,
    int? P2pPeers = null,
    long? UptimeSeconds = null,
    int? ProcessCount = null,

    // ── NOVOS: dados de descoberta P2P ──
    string? PeerId = null,
    IReadOnlyList<string>? Addrs = null,
    int? Port = null,

    // ── NOVOS: separação serviço × UI (PLANO_SEPARACAO_SERVICO_UI.md) ──
    // Enviado apenas pelo serviço (modo SYSTEM): true quando há UI companion
    // conectada via IPC, false quando não há. Absente em agentes antigos/standalone.
    bool? UiOnline = null,

    // Usuário da sessão interativa (console) do Windows, reportado a cada
    // heartbeat. Vazio ("") em agent novo sem sessão interativa; null em agentes
    // antigos (campo ausente) — a distinção permite mostrar "—" em vez de cair
    // para o último usuário conhecido.
    string? LoggedUser = null,

    // Início da sessão interativa atual (UTC). Null quando desconhecido.
    DateTime? LoggedUserSince = null
);

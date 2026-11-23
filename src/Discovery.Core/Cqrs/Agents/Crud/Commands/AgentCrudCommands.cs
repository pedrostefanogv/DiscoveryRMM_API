using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Crud.Commands;

public sealed record ApproveZeroTouchCommand(Guid AgentId) : ICommand<Result<AgentDto>>;
public sealed record CreateAgentCommand(string Name, Guid ClientId, Guid SiteId, Guid? DepartmentId, string? MacAddress, string? Notes) : ICommand<Result<AgentDto>>;
public sealed record UpdateAgentCommand(Guid Id, string? Name, Guid? SiteId, Guid? DepartmentId, string? MacAddress, string? Notes) : ICommand<Result<AgentDto>>;
public sealed record DeleteAgentCommand(Guid Id) : ICommand<Result<VoidResult>>;

/// <summary>Exclusão definitiva (hard delete) de um agente que já está na lixeira.</summary>
public sealed record PurgeAgentCommand(Guid Id, bool Force = false) : ICommand<Result<VoidResult>>;

/// <summary>
/// Pede a descomissionamento remoto do agente (ele se desinstala da máquina).
/// É disparado pelo controller <b>depois</b> do commit do soft delete/purge:
/// o publish NATS é um efeito irreversível e não pode preceder a transação
/// (TransactionBehavior faz rollback de tudo se algo falhar após o publish).
/// </summary>
public sealed record RequestAgentDecommissionCommand(Guid AgentId, string Reason = "trash") : ICommand<Result<AgentDecommissionDispatchDto>>;

/// <summary>
/// Desfecho do pedido de descomissionamento, para o painel poder dizer a
/// VERDADE ao operador: o comando foi publicado no transporte (Sent) ou apenas
/// ficou na fila aguardando o agente reconectar (Queued)? Tratar os dois como
/// sucesso fazia o painel prometer uma desinstalação que nunca aconteceria.
/// </summary>
public sealed record AgentDecommissionDispatchDto(bool Sent, bool Queued, string Message);

/// <summary>Tira o agente da lixeira (limpa <c>DeletedAt</c>).</summary>
public sealed record RestoreAgentCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record AgentDto(
    Guid Id,
    string Hostname,
    string? DisplayName,
    Guid ClientId,
    Guid SiteId,
    string Status,
    string? OperatingSystem,
    string? OsVersion,
    string? AgentVersion,
    string? CommitHash,
    string? MacAddress,
    string? LastIpAddress,
    bool IsOnline,
    DateTime? LastSeenAt,
    bool ZeroTouchPending,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    HeartbeatMetricsDto? HeartbeatMetrics = null,
    DateTime? DeletedAt = null,
    /// <summary>Usuário logado no Windows (ao vivo pelo heartbeat; senão o último persistido).</summary>
    string? LoggedUser = null,
    /// <summary>Início da sessão interativa atual (só quando há heartbeat ao vivo).</summary>
    DateTime? LoggedUserSince = null,
    /// <summary>Reinício pendente (Windows Update, CBS, renomeação, arquivos em uso).</summary>
    bool PendingReboot = false,
    /// <summary>Motivos do reinício pendente (JSON array).</summary>
    string? PendingRebootReasons = null,
    /// <summary>Instante da última sonda de reinício.</summary>
    DateTime? PendingRebootCheckedAt = null,
    /// <summary>
    /// Windows Server? Vem de <c>agent_hardware_info.os_is_server</c> (migration M272).
    /// <c>null</c> = NÃO REPORTADO (agente antigo ou inventário de hardware ainda não
    /// recebido) — nunca significa "não é servidor". O site deve cair para heurística
    /// nesse caso; é o mesmo princípio de <c>Domain</c>/<c>ProductType</c>.
    /// </summary>
    bool? IsServer = null,
    /// <summary>
    /// <c>ProductType</c> canônico do Windows: "WinNT" (cliente), "ServerNT" (servidor
    /// membro), "LanmanNT" (controlador de domínio). Único jeito de distinguir DC de
    /// membro; <c>null</c> = não reportado.
    /// </summary>
    string? ProductType = null,
    /// <summary>
    /// Notebook/tablet? Vem de <c>agent_hardware_info.is_laptop</c> (migration M294).
    /// <c>null</c> = NÃO REPORTADO (agente antigo, VM sem WMI ou coleta inconclusiva) —
    /// nunca "não é notebook". O site só mostra o selo com <c>true</c> explícito.
    /// </summary>
    bool? IsLaptop = null,
    /// <summary>
    /// Vocabulário fechado: "notebook" | "desktop" | "tablet" | "server" | "unknown".
    /// <c>null</c> = não reportado.
    /// </summary>
    string? MachineType = null
);

/// <summary>Página de agentes na lixeira (soft-deleted), com o clientId já resolvido.</summary>
public sealed record DeletedAgentsPageDto(
    IReadOnlyList<AgentDto> Items,
    int Total,
    int Page,
    int PageSize
);

public sealed record HeartbeatMetricsDto(
    double? CpuPercent,
    double? CpuTemperatureCelsius,
    double? MemoryPercent,
    double? DiskPercent,
    double? MemoryTotalGb,
    double? MemoryUsedGb,
    double? DiskTotalGb,
    double? DiskUsedGb,
    double? DiskReadPercent,
    double? DiskWritePercent,
    double? DiskResponseMs,
    int? P2pPeers,
    long? UptimeSeconds,
    int? ProcessCount,
    bool? UiOnline,
    string? IpAddress,
    string? Hostname,
    string? AgentVersion,
    string? CommitHash,
    DateTime? TimestampUtc,
    DateTime? ReceivedAtUtc,
    // ── Energia (bateria × tomada), ao vivo pelo heartbeat ──
    // Ponteiros/nullable: null = não reportado (agente antigo ou leitura
    // incerta). O painel esconde o bloco em vez de afirmar "sem bateria".
    bool? HasBattery = null,
    int? BatteryPercent = null,
    bool? BatteryOnAc = null,
    bool? BatteryCharging = null
);
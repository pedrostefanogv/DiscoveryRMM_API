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
public sealed record RequestAgentDecommissionCommand(Guid AgentId, string Reason = "trash") : ICommand<Result<VoidResult>>;

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
    DateTime? LoggedUserSince = null
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
    DateTime? ReceivedAtUtc
);
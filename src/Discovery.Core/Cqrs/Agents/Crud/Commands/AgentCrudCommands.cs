using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Crud.Commands;

public sealed record ApproveZeroTouchCommand(Guid AgentId) : ICommand<Result<AgentDto>>;
public sealed record CreateAgentCommand(string Name, Guid ClientId, Guid SiteId, Guid? DepartmentId, string? MacAddress, string? Notes) : ICommand<Result<AgentDto>>;
public sealed record UpdateAgentCommand(Guid Id, string? Name, Guid? SiteId, Guid? DepartmentId, string? MacAddress, string? Notes) : ICommand<Result<AgentDto>>;
public sealed record DeleteAgentCommand(Guid Id) : ICommand<Result<VoidResult>>;

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
    HeartbeatMetricsDto? HeartbeatMetrics = null
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
    string? IpAddress,
    string? Hostname,
    string? AgentVersion,
    string? CommitHash,
    DateTime? TimestampUtc,
    DateTime? ReceivedAtUtc
);
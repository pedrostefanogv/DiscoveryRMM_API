using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Sites.PowerManagement.Commands;

/// <summary>Comando de reinicialização em massa para todos os agentes online de um site.</summary>
public sealed record SiteRestartCommand(
    Guid SiteId,
    [property: System.Text.Json.Serialization.JsonPropertyName("delaySeconds")]
    int DelaySeconds = 15,
    [property: System.Text.Json.Serialization.JsonPropertyName("force")]
    bool Force = false,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")]
    string? Message = null) : ICommand<Result<SiteFanoutResponseDto>>;

/// <summary>Comando de desligamento em massa para todos os agentes online de um site.</summary>
public sealed record SiteShutdownCommand(
    Guid SiteId,
    [property: System.Text.Json.Serialization.JsonPropertyName("delaySeconds")]
    int DelaySeconds = 30,
    [property: System.Text.Json.Serialization.JsonPropertyName("force")]
    bool Force = false,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")]
    string? Message = null) : ICommand<Result<SiteFanoutResponseDto>>;

/// <summary>Comando Wake-on-LAN em massa para agentes offline do site (envia magic packet para todos os MACs).</summary>
public sealed record SiteWakeOnLanCommand(Guid SiteId) : ICommand<Result<SiteWakeOnLanResponseDto>>;

public sealed record SiteFanoutResponseDto(
    Guid DispatchId,
    string Subject,
    string TargetScope,
    string? IdempotencyKey,
    int OnlineAgents);

public sealed record SiteWakeOnLanResponseDto(
    Guid DispatchId,
    int TargetCount,
    int OnlineRelayCount,
    IReadOnlyList<string> TargetAgentNames,
    IReadOnlyList<string> MacAddresses,
    DateTime ExpiresAtUtc);
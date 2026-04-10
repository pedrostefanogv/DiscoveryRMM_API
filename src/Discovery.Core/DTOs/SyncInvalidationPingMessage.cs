using System.Text.Json.Serialization;

namespace Discovery.Core.DTOs;

public sealed class SyncInvalidationPingMessage
{
    [JsonPropertyName("eventId")]
    public Guid EventId { get; init; }

    [JsonPropertyName("agentId")]
    public Guid AgentId { get; init; }

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = "sync.invalidated";

    [JsonPropertyName("resource")]
    public string Resource { get; init; } = string.Empty;

    [JsonPropertyName("scopeType")]
    public string ScopeType { get; init; } = string.Empty;

    [JsonPropertyName("scopeId")]
    public Guid? ScopeId { get; init; }

    [JsonPropertyName("installationType")]
    public string? InstallationType { get; init; }

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("changedAtUtc")]
    public DateTime ChangedAtUtc { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    public static SyncInvalidationPingMessage FromDto(SyncInvalidationPingDto ping)
    {
        return new SyncInvalidationPingMessage
        {
            EventId = ping.EventId,
            AgentId = ping.AgentId,
            EventType = ping.EventType,
            Resource = ping.Resource.ToString(),
            ScopeType = ping.ScopeType.ToString(),
            ScopeId = ping.ScopeId,
            InstallationType = ping.InstallationType?.ToString(),
            Revision = ping.Revision,
            Reason = ping.Reason,
            ChangedAtUtc = ping.ChangedAtUtc,
            CorrelationId = ping.CorrelationId
        };
    }
}
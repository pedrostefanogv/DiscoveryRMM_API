using System.Text.Json.Serialization;

namespace Discovery.Core.DTOs;

public sealed record SetAgentMaintenanceRequest(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record SetAgentMaintenanceResponse(
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("maintenanceEnabled")] bool MaintenanceEnabled,
    [property: JsonPropertyName("effectiveStatus")] string EffectiveStatus,
    [property: JsonPropertyName("changedAtUtc")] DateTime ChangedAtUtc,
    [property: JsonPropertyName("reason")] string? Reason);

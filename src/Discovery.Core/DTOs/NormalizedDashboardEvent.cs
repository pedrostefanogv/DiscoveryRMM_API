using System.Text.Json;

namespace Discovery.Core.DTOs;

public sealed record NormalizedDashboardEvent(
    string EventType,
    JsonElement? Data,
    DateTime TimestampUtc,
    Guid? ClientId,
    Guid? SiteId);

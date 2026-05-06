using System.Text.Json.Serialization;

namespace Discovery.Core.DTOs;

/// <summary>
/// Pong global emitido pelo servidor para sinalizar disponibilidade no realtime.
/// Nao e persistido em JetStream.
/// </summary>
public sealed class GlobalPongMessage
{
    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = "pong";

    [JsonPropertyName("serverTimeUtc")]
    public DateTime ServerTimeUtc { get; init; }

    // null/omitted = estado nao determinado (default atual); true = servidor sobrecarregado.
    [JsonPropertyName("serverOverloaded")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ServerOverloaded { get; init; }
}

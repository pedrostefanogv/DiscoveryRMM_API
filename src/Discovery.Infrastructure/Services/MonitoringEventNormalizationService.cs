using System.Text;
using System.Text.Json;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public class MonitoringEventNormalizationService : IMonitoringEventNormalizationService
{
    private static readonly HashSet<string> VolatilePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp",
        "occurredAt",
        "createdAt",
        "updatedAt",
        "requestId",
        "correlationId",
        "nonce",
        "sequence",
        "counter",
        "elapsedMs",
        "durationMs",
        "receivedAt",
        "lastSeenAt"
    };

    public string NormalizePayloadJson(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return "{}";

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonical(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(payloadJson.Trim());
        }
    }

    public string SerializeLabels(IReadOnlyCollection<string>? labels)
    {
        var normalized = (labels ?? [])
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return JsonSerializer.Serialize(normalized);
    }

    public IReadOnlyList<string> DeserializeLabels(string? labelsJson)
    {
        if (string.IsNullOrWhiteSpace(labelsJson))
            return [];

        try
        {
            var labels = JsonSerializer.Deserialize<List<string>>(labelsJson) ?? [];
            return labels
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (VolatilePropertyNames.Contains(property.Name))
                        continue;

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
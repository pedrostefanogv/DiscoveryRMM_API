using System.Text.Json;

namespace Discovery.Core.Cqrs.Patches;

/// <summary>Helpers de JSON usados pelos DTOs/handlers de patches.</summary>
public static class PatchJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Lê um array de strings de um JSON armazenado em coluna.</summary>
    public static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var values = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        values.Add(value.Trim());
                }
            }
            return values;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serializa uma lista de strings para JSON (null quando vazia).</summary>
    public static string? SerializeStringArray(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return null;
        return JsonSerializer.Serialize(values, Options);
    }

    /// <summary>
    /// Lê um mapa string → int de um JSON armazenado em coluna
    /// (ex.: <c>skippedReasons</c> = {"driver":2,"no_identity":1}).
    /// Null/vazio/inválido devolve null — nunca um mapa vazio inventado.
    /// </summary>
    public static IReadOnlyDictionary<string, int>? ParseIntDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var values = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out var value))
                {
                    continue;
                }
                var key = property.Name.Trim();
                if (key.Length > 0)
                    values[key] = value;
            }
            return values.Count == 0 ? null : values;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serializa um mapa string → int (null quando vazio).</summary>
    public static string? SerializeIntDictionary(IReadOnlyDictionary<string, int>? values)
    {
        if (values is null || values.Count == 0)
            return null;
        return JsonSerializer.Serialize(values, Options);
    }
}

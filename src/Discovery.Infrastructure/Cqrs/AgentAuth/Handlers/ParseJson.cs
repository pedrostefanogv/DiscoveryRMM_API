using System.Text.Json;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

/// <summary>
/// Low-level JSON element access helpers used by hardware/software inventory parsers.
/// </summary>
internal static class ParseJson
{
    public static string? GetString(JsonElement obj, string propertyName)
        => TryGetProperty(obj, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string? GetString(JsonElement obj, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(obj, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
                continue;

            return value.GetString();
        }

        return null;
    }

    public static long GetLong(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    public static long GetLong(JsonElement obj, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(obj, propertyName, out var value))
                continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.Number when value.TryGetInt64(out var number):
                    return number;
                case JsonValueKind.String when long.TryParse(value.GetString(), out var parsed):
                    return parsed;
            }
        }

        return 0;
    }

    public static int GetInt(JsonElement obj, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(obj, propertyName, out var value))
                continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.Number when value.TryGetInt32(out var number):
                    return number;
                case JsonValueKind.String when int.TryParse(value.GetString(), out var parsed):
                    return parsed;
            }
        }

        return 0;
    }

    public static int? GetNullableInt(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public static bool GetBool(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => false
        };
    }

    public static string? GetStringProperty(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    public static decimal? GetNullableDecimal(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public static DateTime? GetNullableDateTime(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String when DateTime.TryParse(value.GetString(), out var parsed) => parsed.ToUniversalTime(),
            _ => null
        };
    }

    public static bool TryGetArrayProperty(JsonElement obj, out JsonElement array, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                array = value;
                return true;
            }
        }

        array = default;
        return false;
    }

    /// <summary>
    /// Acesso seguro a propriedade. <c>JsonElement.TryGetProperty</c> NÃO é
    /// tolerante a tipos: em um elemento que não é objeto (array, string,
    /// número...) ele lança InvalidOperationException
    /// ("requires an element of type 'Object'"). Como estes helpers são
    /// chamados com JSON vindo do agent, qualquer payload inesperado viraria
    /// HTTP 400/500. Aqui um não-objeto simplesmente não tem a propriedade.
    /// </summary>
    private static bool TryGetProperty(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        return obj.TryGetProperty(propertyName, out value);
    }
}
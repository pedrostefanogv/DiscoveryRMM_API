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

    public static long? GetNullableLong(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
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

    /// <summary>
    /// Acesso a propriedade que deve ser um OBJETO (ex.: <c>serverHardware</c>,
    /// <c>hyperV</c>, <c>cluster</c>, <c>storageRds</c>). Devolve false quando a
    /// chave não existe ou não é objeto — arrays/strings não são erro.
    /// </summary>
    public static bool TryGetObjectProperty(JsonElement obj, out JsonElement nested, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                nested = value;
                return true;
            }
        }

        nested = default;
        return false;
    }

    /// <summary>
    /// Booleano que distingue "não reportado" (null) de false. Aceita booleano
    /// JSON nativo e, por tolerância, as formas textuais "true"/"1"/"sim".
    /// </summary>
    public static bool? GetNullableBool(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "sim" or "yes" => true,
                "false" or "0" or "nao" or "não" or "no" => false,
                _ => null
            },
            _ => null
        };
    }

    /// <summary>
    /// Serializa uma lista aninhada (nós/recursos do cluster, discos degradados)
    /// para guardar em coluna JSON. Devolve null quando ausente/vazia, para não
    /// poluir a coluna com "[]".
    /// </summary>
    public static string? SerializeArrayProperty(JsonElement obj, string propertyName)
    {
        if (!TryGetProperty(obj, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;

        return value.GetArrayLength() == 0 ? null : value.GetRawText();
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
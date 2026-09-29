using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Core.Helpers;

/// <summary>
/// Le um campo que pode vir como string ("ApplyOnly") ou numero (0).
///
/// Arquivos de import editados a mao costumam usar o valor numerico do enum; sem
/// isto o binding do DTO (`string ApplyMode`) falhava e a importacao inteira
/// retornava erro de desserializacao.
/// </summary>
public sealed class StringOrNumberJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var intValue)
                => intValue.ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Esperado string ou numero, recebido {reader.TokenType}.")
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

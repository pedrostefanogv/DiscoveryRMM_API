using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Core.Cqrs;

/// <summary>
/// Patch semântico para um campo <see cref="Guid"/> anulável: distingue "campo
/// não enviado" (<see cref="IsSet"/> = false) de "enviar null" (IsSet = true,
/// Value = null). Sem isso o update não conseguia desfazer a herança de
/// departamento (D5).
/// </summary>
[JsonConverter(typeof(OptionalGuidJsonConverter))]
public readonly struct OptionalGuid
{
    public bool IsSet { get; }
    public Guid? Value { get; }

    private OptionalGuid(bool isSet, Guid? value)
    {
        IsSet = isSet;
        Value = value;
    }

    /// <summary>Campo ausente no patch.</summary>
    public static OptionalGuid Unset => default;

    /// <summary>Campo presente no patch (com valor ou null explícito).</summary>
    public static OptionalGuid Set(Guid? value) => new(true, value);

    public override string ToString() => IsSet ? Value?.ToString() ?? "null" : "(não enviado)";
}

/// <summary>Serializa/desserializa <see cref="OptionalGuid"/> preservando a presença do campo.</summary>
public sealed class OptionalGuidJsonConverter : JsonConverter<OptionalGuid>
{
    public override OptionalGuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return OptionalGuid.Set(null);

        if (reader.TokenType == JsonTokenType.String
            && reader.GetString() is { } text
            && Guid.TryParse(text, out var value))
        {
            return OptionalGuid.Set(value);
        }

        throw new JsonException("inheritFromGlobalId deve ser um UUID ou null.");
    }

    public override void Write(Utf8JsonWriter writer, OptionalGuid value, JsonSerializerOptions options)
    {
        if (!value.IsSet || value.Value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value.Value);
    }
}

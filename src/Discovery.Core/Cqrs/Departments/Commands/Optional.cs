using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Core.Cqrs.Departments.Commands;

/// <summary>
/// Patch semântico para um valor de tipo primitivo: distingue "campo ausente"
/// (<see cref="IsSet"/> = false) de "campo enviado com null" (IsSet = true,
/// Value = null). É o que permite ao console mandar os 16 campos com o nome
/// base e usar null para voltar a herdar, sem que um campo ausente apague o
/// valor atual.
/// </summary>
[JsonConverter(typeof(OptionalValueJsonConverterFactory))]
public readonly struct OptionalValue<T> where T : struct
{
    public bool IsSet { get; }

    /// <summary>Valor enviado; null explícito significa "herdar" (limpa o override).</summary>
    public T? Value { get; }

    private OptionalValue(bool isSet, T? value)
    {
        IsSet = isSet;
        Value = value;
    }

    /// <summary>Campo ausente no patch (mantém o valor atual).</summary>
    public static OptionalValue<T> Unset => default;

    /// <summary>Campo presente no patch (com valor ou null explícito).</summary>
    public static OptionalValue<T> Set(T? value) => new(true, value);

    public override string ToString()
        => IsSet ? Value?.ToString() ?? "null" : "(não enviado)";
}

/// <summary>
/// Patch semântico para texto: distingue campo ausente de null explícito.
/// Texto vazio/branco é tratado como null (= herdar) pelo handler de update.
/// </summary>
[JsonConverter(typeof(OptionalTextJsonConverter))]
public readonly struct OptionalText
{
    public bool IsSet { get; }

    /// <summary>Texto enviado; null explícito significa "herdar".</summary>
    public string? Value { get; }

    private OptionalText(bool isSet, string? value)
    {
        IsSet = isSet;
        Value = value;
    }

    /// <summary>Campo ausente no patch (mantém o valor atual).</summary>
    public static OptionalText Unset => default;

    /// <summary>Campo presente no patch (com valor ou null explícito).</summary>
    public static OptionalText Set(string? value) => new(true, value);

    public override string ToString() => IsSet ? Value ?? "null" : "(não enviado)";
}

/// <summary>Cria o conversor de <see cref="OptionalValue{T}"/> para o tipo concreto.</summary>
public sealed class OptionalValueJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType
           && typeToConvert.GetGenericTypeDefinition() == typeof(OptionalValue<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(OptionalValueJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

/// <summary>Lê null como "presente e nulo" (herdar) e primitivos usuais.</summary>
public sealed class OptionalValueJsonConverter<T> : JsonConverter<OptionalValue<T>>
    where T : struct
{
    public override OptionalValue<T> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return OptionalValue<T>.Set(null);

        object value = Type.GetTypeCode(typeof(T)) switch
        {
            TypeCode.Boolean => reader.GetBoolean(),
            TypeCode.Int32 => reader.GetInt32(),
            TypeCode.Int64 => reader.GetInt64(),
            TypeCode.Decimal => reader.GetDecimal(),
            TypeCode.Double => reader.GetDouble(),
            TypeCode.Single => reader.GetSingle(),
            _ when typeof(T) == typeof(Guid) => reader.GetGuid(),
            _ => throw new JsonException($"Tipo {typeof(T).Name} não suportado em OptionalValue<T>.")
        };

        return OptionalValue<T>.Set((T)value);
    }

    public override void Write(
        Utf8JsonWriter writer, OptionalValue<T> value, JsonSerializerOptions options)
    {
        if (!value.IsSet || value.Value is null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (value.Value)
        {
            case bool flag: writer.WriteBooleanValue(flag); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case float number: writer.WriteNumberValue(number); break;
            case Guid guid: writer.WriteStringValue(guid); break;
            default: JsonSerializer.Serialize(writer, value.Value, options); break;
        }
    }
}

/// <summary>Lê null como "presente e nulo" (herdar) e string como valor próprio.</summary>
public sealed class OptionalTextJsonConverter : JsonConverter<OptionalText>
{
    public override OptionalText Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return OptionalText.Set(null);

        if (reader.TokenType == JsonTokenType.String)
            return OptionalText.Set(reader.GetString());

        throw new JsonException("Campo de texto deve ser string ou null.");
    }

    public override void Write(
        Utf8JsonWriter writer, OptionalText value, JsonSerializerOptions options)
    {
        if (!value.IsSet || value.Value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value);
    }
}

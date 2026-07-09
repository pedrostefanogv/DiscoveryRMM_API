using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Api.Serialization;

/// <summary>
/// JSON converter factory for <see cref="Core.Cqrs.Result{TResponse}"/>.
/// On success, serializes only the unwrapped <c>Value</c>.
/// On failure, serializes <c>{"errors": [...]}</c> with HTTP status mapping.
/// </summary>
public sealed class ResultJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsGenericType)
            return false;

        var genericDef = typeToConvert.GetGenericTypeDefinition();
        return genericDef == typeof(Core.Cqrs.Result<>);
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(ResultJsonConverter<>).MakeGenericType(valueType);
        return (JsonConverter?)Activator.CreateInstance(converterType);
    }
}

/// <summary>
/// Custom JSON converter for <see cref="Core.Cqrs.Result{T}"/>.
/// </summary>
internal sealed class ResultJsonConverter<T> : JsonConverter<Core.Cqrs.Result<T>> where T : notnull
{
    public override Core.Cqrs.Result<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Deserialization: check if payload contains "errors" key
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array)
            {
                var errors = JsonSerializer.Deserialize<List<Core.Cqrs.Error>>(errorsElement.GetRawText(), options)
                    ?? new List<Core.Cqrs.Error>();
                return Core.Cqrs.Result<T>.Failure(errors);
            }

            // Otherwise try to deserialize as T (success case)
            var value = JsonSerializer.Deserialize<T>(root.GetRawText(), options);
            if (value is not null)
                return Core.Cqrs.Result<T>.Success(value);
        }

        // Fallback: treat as success with default
        var fallback = JsonSerializer.Deserialize<T>(ref reader, options);
        return fallback is not null
            ? Core.Cqrs.Result<T>.Success(fallback)
            : Core.Cqrs.Result<T>.Failure(new Core.Cqrs.Error("Deserialization", "Could not deserialize result."));
    }

    public override void Write(Utf8JsonWriter writer, Core.Cqrs.Result<T> result, JsonSerializerOptions options)
    {
        if (result.IsSuccess)
        {
            // Unwrap: serialize Value directly
            JsonSerializer.Serialize(writer, result.Value, options);
        }
        else
        {
            // Serialize errors array
            writer.WriteStartObject();
            writer.WritePropertyName("errors");
            JsonSerializer.Serialize(writer, result.Errors, options);
            writer.WriteEndObject();
        }
    }
}

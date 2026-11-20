using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Api.Services;

internal static class PayloadJsonHelpers
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static bool TryGetRequiredString(
        JsonElement payload,
        string propertyName,
        out string value,
        out string validationError,
        bool allowEmpty = false)
    {
        value = string.Empty;
        validationError = string.Empty;

        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            validationError = $"field '{propertyName}' is required.";
            return false;
        }

        if (!TryReadString(element, out value))
        {
            validationError = $"field '{propertyName}' must be a string.";
            return false;
        }

        value = value.Trim();
        if (!allowEmpty && value.Length == 0)
        {
            validationError = $"field '{propertyName}' must be non-empty.";
            return false;
        }

        return true;
    }

    internal static bool TryGetRequiredGuid(
        JsonElement payload,
        string propertyName,
        out Guid value,
        out string validationError)
    {
        value = Guid.Empty;
        validationError = string.Empty;

        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            validationError = $"field '{propertyName}' is required.";
            return false;
        }

        if (!TryReadString(element, out var raw) || !Guid.TryParse(raw, out value))
        {
            validationError = $"field '{propertyName}' must be a valid GUID string.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// GUID OPCIONAL: ausente/nulo/vazio devolve null; string inválida é erro.
    /// </summary>
    internal static bool TryGetOptionalGuid(
        JsonElement payload,
        string propertyName,
        out Guid? value,
        out string validationError)
    {
        value = null;
        validationError = string.Empty;

        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;

        if (!TryReadString(element, out var raw))
        {
            validationError = $"field '{propertyName}' must be a valid GUID string.";
            return false;
        }

        if (raw.Length == 0)
            return true;

        if (!Guid.TryParse(raw, out var parsed))
        {
            validationError = $"field '{propertyName}' must be a valid GUID string.";
            return false;
        }

        value = parsed;
        return true;
    }

    internal static bool TryReadPositiveInt(JsonElement element, out int value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetInt32(out value) || value <= 0)
                return false;

            return true;
        }

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value > 0)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lê um inteiro >= 0 (diferente de <see cref="TryReadPositiveInt"/>, aceita
    /// 0). Usado por comandos que não têm atraso por padrão, como o poweraction.
    /// </summary>
    internal static bool TryReadNonNegativeInt(JsonElement element, out int value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out value) && value >= 0;

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value >= 0)
        {
            return true;
        }

        return false;
    }

    internal static bool TryReadString(JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString() ?? string.Empty;
        return true;
    }

    internal static bool TryParseIsoUtc(string raw, out DateTime parsedUtc)
    {
        parsedUtc = default;
        if (!DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return false;
        }

        parsedUtc = dto.UtcDateTime;
        return true;
    }
}

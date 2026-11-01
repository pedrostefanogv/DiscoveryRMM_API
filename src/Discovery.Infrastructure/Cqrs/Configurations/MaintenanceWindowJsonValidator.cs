using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Infrastructure.Cqrs.Configurations;

/// <summary>
/// Valida o CONTEÚDO do blob <c>maintenanceWindowJson</c> (Server/Client/Site) antes
/// de persistir. O contrato suporta duas formas de janela:
/// - legado: apenas <c>startUtc</c> ("HH:mm" em UTC);
/// - nova: <c>timeZone</c> (IANA, ex. "America/Sao_Paulo") + <c>startTime</c>
///   ("HH:mm" em hora local na timeZone).
/// Sem esta validação, um timeZone IANA com typo fazia o agent Go cair em UTC
/// silenciosamente (ParseMaintenanceWindow tolera valores inválidos).
/// O blob nunca é reescrito aqui — apenas validado; o agent recebe o JSON
/// integral via AgentConfigurationHandlers.TryDeserializeJson.
/// </summary>
/// <remarks>
/// IDs IANA: TimeZoneInfo.FindSystemTimeZoneById resolve IDs IANA
/// ("America/Sao_Paulo") nativamente no .NET 6+ em Linux/containers e no Windows
/// via mapeamento integrado de ICU/registry — sem fallback adicional necessário.
/// IDs Windows ("E. South America Standard Time") também são aceitos, pois o .NET
/// 8+ resolve os dois estilos em qualquer plataforma.
/// </remarks>
public static class MaintenanceWindowJsonValidator
{
    public const string PatchKey = "MaintenanceWindowJson";

    /// <summary>Retorna erros de validação do blob; vazio significa válido.</summary>
    public static IReadOnlyList<string> Validate(string? json)
    {
        // null/vazio = herdar do nível acima (ou janela desligada) — válido.
        if (string.IsNullOrWhiteSpace(json))
            return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return [$"maintenanceWindowJson: JSON inválido ({ex.Message})."];
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                return ["maintenanceWindowJson: o JSON deve ser um objeto."];

            var root = document.RootElement;
            var errors = new List<string>();

            if (TryGetString(root, "timeZone", out var timeZone) && !string.IsNullOrWhiteSpace(timeZone))
            {
                try
                {
                    TimeZoneInfo.FindSystemTimeZoneById(timeZone!);
                }
                catch (TimeZoneNotFoundException)
                {
                    errors.Add($"maintenanceWindowJson.timeZone inválido: {timeZone}");
                }
                catch (InvalidTimeZoneException)
                {
                    errors.Add($"maintenanceWindowJson.timeZone inválido: {timeZone}");
                }
            }

            if (TryGetString(root, "startTime", out var startTime) && !string.IsNullOrWhiteSpace(startTime)
                && !IsHourMinute(startTime!))
            {
                errors.Add($"maintenanceWindowJson.startTime inválido: {startTime} (esperado HH:mm, 00:00-23:59)");
            }

            return errors;
        }
    }

    /// <summary>
    /// Valida o valor bruto vindo de um PATCH (Dictionary&lt;string, object&gt;).
    /// O valor chega como string ou JsonElement (o controller deserializa o corpo).
    /// </summary>
    public static IReadOnlyList<string> ValidatePatchValue(object? value)
    {
        switch (value)
        {
            case null:
            case string s when string.IsNullOrWhiteSpace(s):
                return [];
            case string text:
                return Validate(text);
            case JsonElement { ValueKind: JsonValueKind.Null }:
            case JsonElement { ValueKind: JsonValueKind.Undefined }:
                return [];
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return Validate(element.GetString());
            case JsonElement element:
                return Validate(element.GetRawText());
            default:
                return Validate(value.ToString());
        }
    }

    /// <summary>
    /// Valida o blob de um objeto de configuração completo (PUT/Update).
    /// </summary>
    public static IReadOnlyList<Error> ValidateConfiguration(
        ServerConfiguration server)
        => ValidateAsErrors(server.MaintenanceWindowJson);

    public static IReadOnlyList<Error> ValidateConfiguration(
        ClientConfiguration client)
        => ValidateAsErrors(client.MaintenanceWindowJson);

    public static IReadOnlyList<Error> ValidateConfiguration(
        SiteConfiguration site)
        => ValidateAsErrors(site.MaintenanceWindowJson);

    /// <summary>
    /// Valida o valor de <c>maintenanceWindowJson</c> dentro de um patch, se
    /// presente (busca case-insensitive). Erros viram <c>Error.Validation</c>.
    /// </summary>
    public static IReadOnlyList<Error> ValidatePatch(
        IReadOnlyDictionary<string, object> updates)
    {
        foreach (var (key, value) in updates)
        {
            if (!key.Equals(PatchKey, StringComparison.OrdinalIgnoreCase))
                continue;

            return ValidatePatchValue(value)
                .Select(message => Error.Validation(PatchKey, message))
                .ToArray();
        }

        return [];
    }

    private static IReadOnlyList<Error> ValidateAsErrors(string? json)
        => Validate(json)
            .Select(message => Error.Validation(PatchKey, message))
            .ToArray();

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (root.ValueKind is not JsonValueKind.Object)
            return false;

        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind is JsonValueKind.String)
            {
                value = property.Value.GetString();
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>Regra "HH:mm": dois dígitos, dois dígitos, 00:00–23:59.</summary>
    private static bool IsHourMinute(string value)
    {
        if (value.Length != 5 || value[2] != ':')
            return false;

        if (!char.IsAsciiDigit(value[0]) || !char.IsAsciiDigit(value[1])
            || !char.IsAsciiDigit(value[3]) || !char.IsAsciiDigit(value[4]))
            return false;

        var hour = int.Parse(value.AsSpan(0, 2));
        var minute = int.Parse(value.AsSpan(3, 2));
        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }
}

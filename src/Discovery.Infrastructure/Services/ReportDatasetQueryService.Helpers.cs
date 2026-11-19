using System.Globalization;
using System.Text.Json;
using Discovery.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

public partial class ReportDatasetQueryService
{
    private static JsonElement ParseFilters(string? filtersJson)
    {
        if (string.IsNullOrWhiteSpace(filtersJson))
            return default;

        using var doc = JsonDocument.Parse(filtersJson);
        return doc.RootElement.Clone();
    }

    private static int GetLimit(JsonElement filters)
    {
        // allRows nao pode virar Take(int.MaxValue): o EF remove o LIMIT e a
        // consulta materializa tudo em memoria (pico/DoS). O teto duro mantem o
        // comportamento "todos os registros" com um limite de seguranca.
        if (GetBool(filters, "allRows") is true)
            return HardLimit;

        var value = GetInt(filters, "limit");
        if (value is null)
            return DefaultLimit;

        // limit <= 0 e entrada invalida: usa o default, nao "sem limite".
        if (value.Value <= 0)
            return DefaultLimit;

        return Math.Clamp(value.Value, 1, MaxLimit);
    }

    private static bool? GetBool(JsonElement filters, string property)
    {
        if (filters.ValueKind != JsonValueKind.Object)
            return null;

        if (!filters.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? GetInt(JsonElement filters, string property)
    {
        if (filters.ValueKind != JsonValueKind.Object)
            return null;

        if (!filters.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static string? GetString(JsonElement filters, string property)
    {
        if (filters.ValueKind != JsonValueKind.Object)
            return null;

        if (!filters.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static Guid? GetGuid(JsonElement filters, string property)
    {
        var value = GetString(filters, property);
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    private static DateTime? GetDateTime(JsonElement filters, string property)
    {
        var value = GetString(filters, property);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Cultura invariante + UTC: DateTime com Kind=Unspecified estoura no
        // Npgsql ao comparar com colunas timestamptz, e a cultura do servidor
        // mudava a interpretacao de datas ambiguas.
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dateTime)
            ? dateTime
            : null;
    }

    private static bool GetSortDescending(JsonElement filters, bool defaultValue)
    {
        var direction = GetString(filters, "orderDirection");
        if (string.IsNullOrWhiteSpace(direction))
            return defaultValue;

        return direction.Equals("desc", StringComparison.OrdinalIgnoreCase);
    }

    private static TEnum GetEnum<TEnum>(JsonElement filters, string property, TEnum defaultValue) where TEnum : struct, Enum
    {
        var value = GetString(filters, property);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var result) ? result : defaultValue;
    }

    private async Task<Dictionary<Guid, string>> GetAutomaticLabelsByAgentAsync(IEnumerable<Guid> agentIds, CancellationToken cancellationToken)
    {
        var uniqueAgentIds = agentIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (uniqueAgentIds.Count == 0)
            return new Dictionary<Guid, string>();

        var labels = await _db.AgentLabels
            .AsNoTracking()
            .Where(item => uniqueAgentIds.Contains(item.AgentId) && item.SourceType == AgentLabelSourceType.Automatic)
            .ToListAsync(cancellationToken);

        return labels
            .GroupBy(item => item.AgentId)
            .ToDictionary(
                group => group.Key,
                group => string.Join(", ", group
                    .Select(item => item.Label)
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)));
    }
}

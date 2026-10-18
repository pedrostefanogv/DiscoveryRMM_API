using System.Globalization;
using System.Text.Json;

namespace Discovery.Core.Helpers;

/// <summary>
/// Janela de manutenção das políticas de patch.
/// Formato JSON: {"days":[1..7],"start":"22:00","end":"05:00","timezone":"America/Sao_Paulo"}.
/// <c>days</c> usa o padrão ISO (1 = segunda ... 7 = domingo).
/// </summary>
public static class PatchMaintenanceWindow
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public sealed record Window(IReadOnlyList<int> Days, TimeOnly Start, TimeOnly End, string? Timezone);

    /// <summary>Interpreta o JSON da janela. Retorna null quando inválido/ausente.</summary>
    public static Window? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var root = document.RootElement;
            var days = new List<int>();
            if (root.TryGetProperty("days", out var daysElement) && daysElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var dayElement in daysElement.EnumerateArray())
                {
                    if (dayElement.ValueKind == JsonValueKind.Number &&
                        dayElement.TryGetInt32(out var day) &&
                        day is >= 1 and <= 7)
                    {
                        days.Add(day);
                    }
                }
            }
            if (days.Count == 0)
                days.AddRange([1, 2, 3, 4, 5, 6, 7]);

            var start = ParseTime(root, "start") ?? new TimeOnly(22, 0);
            var end = ParseTime(root, "end") ?? new TimeOnly(5, 0);
            var timezone = root.TryGetProperty("timezone", out var tzElement) &&
                           tzElement.ValueKind == JsonValueKind.String
                ? tzElement.GetString()
                : null;

            return new Window(days, start, end, timezone);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Indica se o instante está dentro da janela. Quando o JSON não define
    /// timezone, usa <paramref name="fallbackTimezone"/> (normalmente o timezone
    /// do site) antes de cair para UTC.
    /// </summary>
    public static bool IsOpen(string? json, DateTime utcNow, string? fallbackTimezone = null)
    {
        var window = Parse(json);
        if (window is null)
            return false;

        var timezone = string.IsNullOrWhiteSpace(window.Timezone) ? fallbackTimezone : window.Timezone;
        var localNow = ToLocal(utcNow, timezone);
        var isoDay = localNow.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)localNow.DayOfWeek;
        if (!window.Days.Contains(isoDay))
            return false;

        var time = TimeOnly.FromDateTime(localNow);
        // Janela que cruza a meia-noite (ex.: 22:00 → 05:00): no dia seguinte, a
        // madrugada ainda pertence à janela iniciada no dia anterior.
        if (window.Start <= window.End)
            return time >= window.Start && time <= window.End;

        if (time >= window.Start)
            return true;

        var previousIsoDay = isoDay == 1 ? 7 : isoDay - 1;
        return time <= window.End && window.Days.Contains(previousIsoDay);
    }

    private static DateTime ToLocal(DateTime utcNow, string? timezone)
    {
        var utc = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        if (string.IsNullOrWhiteSpace(timezone))
            return utc;

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        }
        catch (TimeZoneNotFoundException)
        {
            return utc;
        }
        catch (InvalidTimeZoneException)
        {
            return utc;
        }
    }

    private static TimeOnly? ParseTime(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return null;
        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return TimeOnly.TryParseExact(raw, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)
            ? time
            : null;
    }
}

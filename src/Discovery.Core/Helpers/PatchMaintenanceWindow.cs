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


    /// <summary>Ocorrência concreta de uma janela de manutenção, em UTC.</summary>
    public sealed record WindowOccurrence(DateTime StartUtc, DateTime EndUtc, string Id)
    {
        /// <summary>Instante (UTC) em que a janela abre.</summary>
        public DateTime Start => StartUtc;
        /// <summary>Duração efetiva da ocorrência.</summary>
        public TimeSpan Duration => EndUtc - StartUtc;
    }

    /// <summary>Horizonte máximo da projeção (dias).</summary>
    public const int MaxProjectionDays = 30;

    /// <summary>
    /// Projeta a ocorrência de janela que contém <paramref name="utcNow"/> ou a
    /// próxima a abrir, dentro de um horizonte (WU-18).
    ///
    /// Antes só existia <see cref="IsOpen"/> ("está aberta agora?"), o que
    /// impedia a UI e o job de responder "quando abre a próxima" — e obrigava o
    /// operador a calcular de cabeça. Regras:
    ///  • horário é interpretado no timezone do JSON, senão no do site, senão UTC;
    ///  • janela que cruza a meia-noite pertence ao dia em que ABRIU;
    ///  • horário local inexistente (gap de DST) é deslocado para frente até
    ///    existir — a janela abre no primeiro instante válido, nunca "nunca";
    ///  • horário local ambíguo (fim de DST) usa a PRIMEIRA ocorrência;
    ///  • <c>Id</c> é estável por ocorrência (UTC da abertura), para UI e job
    ///    concordarem no limite.
    /// </summary>
    public static WindowOccurrence? NextOccurrence(
        string? json,
        DateTime utcNow,
        string? fallbackTimezone = null,
        int horizonDays = MaxProjectionDays)
    {
        var window = Parse(json);
        if (window is null)
            return null;

        var utc = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        var timezone = ResolveTimezone(string.IsNullOrWhiteSpace(window.Timezone) ? fallbackTimezone : window.Timezone);
        var horizon = Math.Clamp(horizonDays <= 0 ? MaxProjectionDays : horizonDays, 1, MaxProjectionDays);

        // Se já estamos dentro de uma janela, devolvemos a ocorrência corrente.
        if (IsOpen(json, utc, fallbackTimezone))
        {
            var current = CurrentOccurrence(window, timezone, utc);
            if (current is not null)
                return current;
        }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utc, timezone);
        for (var offset = 0; offset <= horizon; offset++)
        {
            var localDate = localNow.Date.AddDays(offset);
            var isoDay = localDate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)localDate.DayOfWeek;
            if (!window.Days.Contains(isoDay))
                continue;

            var startUtc = LocalToUtc(localDate + window.Start.ToTimeSpan(), timezone);
            var endUtc = WindowEndUtc(localDate, window, timezone);
            if (endUtc <= startUtc)
                continue;

            if (endUtc <= utc)
                continue;

            if (startUtc <= utc && utc < endUtc)
                return new WindowOccurrence(startUtc, endUtc, BuildWindowId(startUtc));

            if (startUtc > utc)
                return new WindowOccurrence(startUtc, endUtc, BuildWindowId(startUtc));
        }

        return null;
    }

    /// <summary>Identificador estável da ocorrência (UTC da abertura).</summary>
    public static string BuildWindowId(DateTime startUtc) =>
        "win-" + startUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static WindowOccurrence? CurrentOccurrence(Window window, TimeZoneInfo timezone, DateTime utc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utc, timezone);
        // A ocorrência corrente pode ter aberto hoje ou no dia anterior
        // (janela que cruza a meia-noite).
        foreach (var dayOffset in new[] { 0, -1 })
        {
            var localDate = localNow.Date.AddDays(dayOffset);
            var startUtc = LocalToUtc(localDate + window.Start.ToTimeSpan(), timezone);
            var endUtc = WindowEndUtc(localDate, window, timezone);
            if (startUtc <= utc && utc < endUtc)
                return new WindowOccurrence(startUtc, endUtc, BuildWindowId(startUtc));
        }
        return null;
    }

    private static DateTime WindowEndUtc(DateTime localDate, Window window, TimeZoneInfo timezone)
    {
        var endLocal = localDate + window.End.ToTimeSpan();
        // Janela que cruza a meia-noite termina no dia seguinte.
        if (window.End <= window.Start)
            endLocal = endLocal.AddDays(1);
        return LocalToUtc(endLocal, timezone);
    }

    private static DateTime LocalToUtc(DateTime local, TimeZoneInfo timezone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // Gap de DST: o horário local não existe. Avança até existir, para a
        // janela abrir no primeiro instante válido em vez de desaparecer.
        for (var i = 0; i < 4 && timezone.IsInvalidTime(unspecified); i++)
            unspecified = unspecified.AddMinutes(15);

        if (timezone.IsInvalidTime(unspecified))
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timezone);

        // Ambiguidade (fim de DST): a PRIMEIRA ocorrência é a escolhida.
        if (timezone.IsAmbiguousTime(unspecified))
        {
            var offsets = timezone.GetAmbiguousTimeOffsets(unspecified);
            var first = offsets.Max();
            return DateTime.SpecifyKind(unspecified - first, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, timezone);
    }

    private static TimeZoneInfo ResolveTimezone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
            return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
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

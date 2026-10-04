namespace Discovery.Core.Helpers;

/// <summary>
/// Converte entre o modelo da UI (frequencia/dia/hora) e a expressao cron do
/// Quartz usada por ReportSchedule, e calcula a proxima ocorrencia em UTC.
/// Assim o agendamento criado pela UI ja nasce com NextTriggerAt correto.
/// </summary>
public static class ReportScheduleCalculator
{
    public const int Daily = 0;
    public const int Weekly = 1;
    public const int Monthly = 2;

    public static string BuildCron(int frequency, int? dayOfWeek, int? dayOfMonth, int hourUtc, int minuteUtc)
    {
        var hour = Math.Clamp(hourUtc, 0, 23);
        var minute = Math.Clamp(minuteUtc, 0, 59);

        return frequency switch
        {
            Weekly => $"0 {minute} {hour} ? * {Math.Clamp(dayOfWeek ?? 1, 1, 7)}",
            Monthly => $"0 {minute} {hour} {Math.Clamp(dayOfMonth ?? 1, 1, 31)} * ?",
            _ => $"0 {minute} {hour} * * ?"
        };
    }

    /// <summary>Interpreta o cron gerado por BuildCron; formatos desconhecidos caem em diario.</summary>
    public static (int Frequency, int? DayOfWeek, int? DayOfMonth, int HourUtc, int MinuteUtc) ParseCron(string? cron)
    {
        var fallback = (Frequency: Daily, DayOfWeek: (int?)null, DayOfMonth: (int?)null, HourUtc: 8, MinuteUtc: 0);
        if (string.IsNullOrWhiteSpace(cron))
            return fallback;

        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6)
            return fallback;

        var minute = int.TryParse(parts[1], out var parsedMinute) ? Math.Clamp(parsedMinute, 0, 59) : 0;
        var hour = int.TryParse(parts[2], out var parsedHour) ? Math.Clamp(parsedHour, 0, 23) : 0;

        var dayOfMonthField = parts[3];
        var dayOfWeekField = parts[5];

        if (dayOfWeekField != "?" && int.TryParse(dayOfWeekField, out var parsedDayOfWeek))
            return (Weekly, Math.Clamp(parsedDayOfWeek, 1, 7), null, hour, minute);

        if (dayOfMonthField != "?" && dayOfMonthField != "*" && int.TryParse(dayOfMonthField, out var parsedDayOfMonth))
            return (Monthly, null, Math.Clamp(parsedDayOfMonth, 1, 31), hour, minute);

        return (Daily, null, null, hour, minute);
    }

    /// <summary>Proxima ocorrencia em UTC, ou null se nao for possivel calcular.</summary>
    public static DateTime? ComputeNextOccurrenceUtc(
        int frequency,
        int? dayOfWeek,
        int? dayOfMonth,
        int hourUtc,
        int minuteUtc,
        DateTime fromUtc)
    {
        var hour = Math.Clamp(hourUtc, 0, 23);
        var minute = Math.Clamp(minuteUtc, 0, 59);
        var from = fromUtc.Kind == DateTimeKind.Utc ? fromUtc : fromUtc.ToUniversalTime();

        switch (frequency)
        {
            case Weekly:
            {
                var targetDay = QuartzDayToDotNet(dayOfWeek ?? 1);
                var baseDate = from.Date;
                for (var offset = 0; offset <= 7; offset++)
                {
                    var candidate = baseDate.AddDays(offset).AddHours(hour).AddMinutes(minute);
                    if (candidate.DayOfWeek == targetDay && candidate > from)
                        return candidate;
                }
                return null;
            }

            case Monthly:
            {
                var wantedDay = Math.Clamp(dayOfMonth ?? 1, 1, 31);
                var monthStart = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                for (var offset = 0; offset <= 12; offset++)
                {
                    var month = monthStart.AddMonths(offset);
                    var day = Math.Min(wantedDay, DateTime.DaysInMonth(month.Year, month.Month));
                    var candidate = new DateTime(month.Year, month.Month, day, hour, minute, 0, DateTimeKind.Utc);
                    if (candidate > from)
                        return candidate;
                }
                return null;
            }

            default:
            {
                var candidate = from.Date.AddHours(hour).AddMinutes(minute);
                return candidate > from ? candidate : candidate.AddDays(1);
            }
        }
    }

    /// <summary>Quartz: 1=domingo..7=sabado -> .NET DayOfWeek (domingo=0).</summary>
    private static DayOfWeek QuartzDayToDotNet(int quartzDay)
        => (DayOfWeek)((Math.Clamp(quartzDay, 1, 7) - 1) % 7);
}

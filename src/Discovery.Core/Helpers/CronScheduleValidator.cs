namespace Discovery.Core.Helpers;

/// <summary>
/// Validador de expressões cron no dialeto usado pelo agent (robfig/cron padrão):
/// 5 campos — minuto (0-59), hora (0-23), dia do mês (1-31), mês (1-12/nomes), dia da semana (0-6/nomes, 0=domingo).
/// Suporta: "*", "?", listas "a,b", intervalos "a-b", passos "*/n" e "a-b/n".
/// </summary>
public static partial class CronScheduleValidator
{
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    public static bool IsValid(string? expression) => TryValidate(expression, out _);

    public static bool TryValidate(string? expression, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Cron expression is empty.";
            return false;
        }

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            error = $"Expected 5 fields (minute hour day-of-month month day-of-week), got {fields.Length}.";
            return false;
        }

        if (!ValidateField(fields[0], 0, 59, allowNames: false, "minute", ref error)) return false;
        if (!ValidateField(fields[1], 0, 23, allowNames: false, "hour", ref error)) return false;
        if (!ValidateField(fields[2], 1, 31, allowNames: false, "day-of-month", ref error)) return false;
        if (!ValidateField(fields[3], 1, 12, allowNames: true, "month", ref error)) return false;
        if (!ValidateField(fields[4], 0, 7, allowNames: true, "day-of-week", ref error)) return false;

        return true;
    }

    private static bool ValidateField(string field, int min, int max, bool allowNames, string fieldName, ref string? error)
    {
        foreach (var part in field.Split(','))
        {
            if (!ValidatePart(part.Trim(), min, max, allowNames, fieldName, ref error))
                return false;
        }

        return true;
    }

    private static bool ValidatePart(string part, int min, int max, bool allowNames, string fieldName, ref string? error)
    {
        if (part.Length == 0)
        {
            error = $"Empty component in {fieldName} field.";
            return false;
        }

        // Passo: "*/n", "a-b/n", "a/n"
        string rangePart = part;
        var stepIdx = part.IndexOf('/');
        if (stepIdx >= 0)
        {
            rangePart = part[..stepIdx];
            var stepText = part[(stepIdx + 1)..];
            if (!int.TryParse(stepText, out var step) || step < 1)
            {
                error = $"Invalid step '{stepText}' in {fieldName} field.";
                return false;
            }
        }

        if (rangePart is "*" or "?")
            return true;

        var dashIdx = rangePart.IndexOf('-');
        if (dashIdx < 0)
            return TryParseValue(rangePart, allowNames, min, max, fieldName, ref error);

        var startText = rangePart[..dashIdx];
        var endText = rangePart[(dashIdx + 1)..];
        if (!TryParseValue(startText, allowNames, min, max, fieldName, out var start, ref error)) return false;
        if (!TryParseValue(endText, allowNames, min, max, fieldName, out var end, ref error)) return false;

        if (start > end)
        {
            error = $"Range start {start} is greater than end {end} in {fieldName} field.";
            return false;
        }

        return true;
    }

    private static bool TryParseValue(string text, bool allowNames, int min, int max, string fieldName, ref string? error)
        => TryParseValue(text, allowNames, min, max, fieldName, out _, ref error);

    private static bool TryParseValue(string text, bool allowNames, int min, int max, string fieldName, out int value, ref string? error)
    {
        value = 0;

        if (int.TryParse(text, out value))
        {
            // robfig/cron aceita 0 e 7 como domingo no campo day-of-week.
            var effectiveMax = fieldName == "day-of-week" ? Math.Max(max, 7) : max;
            if (value < min || value > effectiveMax)
            {
                error = $"Value {value} out of range [{min}-{effectiveMax}] in {fieldName} field.";
                return false;
            }

            return true;
        }

        if (allowNames)
        {
            var normalized = text.ToLowerInvariant();
            var names = fieldName == "month" ? MonthNames : DayNames;
            var idx = Array.FindIndex(names, n => n == normalized);
            if (idx >= 0)
            {
                value = fieldName == "month" ? idx + 1 : idx;
                return true;
            }
        }

        error = $"Invalid value '{text}' in {fieldName} field.";
        return false;
    }
}

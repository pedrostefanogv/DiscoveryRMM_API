using Discovery.Core.Enums;

namespace Discovery.Core.Helpers;

/// <summary>
/// Resolve o formato de relatorio a partir dos valores flexiveis que a API
/// recebe historicamente: numero do enum ("3"), nome ("Markdown"), camelCase
/// ("markdown") ou extensao ("md"). Valores numericos fora do enum (ex.: o
/// extinto Pdf = 1) nao sao resolvidos — quem chama decide o fallback.
/// </summary>
public static class ReportFormatResolver
{
    public static ReportFormat? Resolve(object? value)
    {
        if (value is null)
            return null;

        if (value is int intValue)
            return FromNumber(intValue);

        if (value is long longValue)
            return FromNumber((int)longValue);

        // JsonElement (quando o comando declara object?) cai no ToString().
        var raw = value.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();

        if (int.TryParse(raw, out var numeric))
            return FromNumber(numeric);

        if (Enum.TryParse<ReportFormat>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;

        if (string.Equals(raw, "md", StringComparison.OrdinalIgnoreCase))
            return ReportFormat.Markdown;

        if (string.Equals(raw, "excel", StringComparison.OrdinalIgnoreCase))
            return ReportFormat.Xlsx;

        return null;
    }

    /// <summary>True quando o valor e o legado Pdf = 1 (ou "Pdf"), removido do enum.</summary>
    public static bool IsLegacyPdf(object? value)
    {
        if (value is int intValue) return intValue == 1;
        if (value is long longValue) return longValue == 1;

        var raw = value?.ToString()?.Trim();
        return string.Equals(raw, "1", StringComparison.Ordinal) || string.Equals(raw, "Pdf", StringComparison.OrdinalIgnoreCase);
    }

    public static string SupportedList() => string.Join(", ", Enum.GetNames<ReportFormat>());

    private static ReportFormat? FromNumber(int value)
        => Enum.IsDefined(typeof(ReportFormat), value) ? (ReportFormat)value : null;
}

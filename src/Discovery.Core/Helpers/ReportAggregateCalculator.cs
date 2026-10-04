using System.Text.Json;

namespace Discovery.Core.Helpers;

/// <summary>
/// Calcula as agregacoes de resumo (globais e por grupo) em um unico lugar.
///
/// Antes cada renderer tinha a sua propria copia: o Markdown implementava 6 das
/// 13 agregacoes aceitas pelo ReportLayoutValidator e o HTML 9 — ou seja,
/// "median", "percentile90", "first" e "last" eram aceitos na montagem do
/// template e renderizavam "-" silenciosamente.
/// </summary>
public static class ReportAggregateCalculator
{
    public static object? Compute(
        string? aggregate,
        string? field,
        JsonElement? condition,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var op = aggregate?.Trim().ToLowerInvariant();

        return op switch
        {
            "count" => rows.Count,
            "countdistinct" => CountDistinct(field, rows),
            "sum" => Sum(field, rows),
            "avg" => Average(field, rows),
            "min" => Min(field, rows),
            "max" => Max(field, rows),
            "first" => First(field, rows),
            "last" => Last(field, rows),
            "median" => Median(field, rows),
            "percentile90" => Percentile(field, rows, 90d),
            "countif" => CountIf(field, condition, rows),
            "sumif" => SumIf(field, condition, rows),
            "compliancepercent" => CompliancePercent(field, rows),
            _ => null
        };
    }

    private static object? CountDistinct(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;

        return rows
            .Where(row => row.TryGetValue(field, out var value) && value is not null)
            .Select(row => row[field]?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static decimal Sum(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.IsNullOrWhiteSpace(field))
            return 0m;

        decimal sum = 0;
        foreach (var row in rows)
        {
            if (row.TryGetValue(field, out var value) && value is not null && TryConvertToDecimal(value, out var decimalValue))
                sum += decimalValue;
        }

        return sum;
    }

    private static decimal? Average(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;

        decimal sum = 0;
        var count = 0;
        foreach (var row in rows)
        {
            if (row.TryGetValue(field, out var value) && value is not null && TryConvertToDecimal(value, out var decimalValue))
            {
                sum += decimalValue;
                count++;
            }
        }

        return count > 0 ? sum / count : null;
    }

    private static decimal? Min(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;

        decimal? min = null;
        foreach (var row in rows)
        {
            if (row.TryGetValue(field, out var value) && value is not null && TryConvertToDecimal(value, out var decimalValue)
                && (min is null || decimalValue < min))
            {
                min = decimalValue;
            }
        }

        return min;
    }

    private static decimal? Max(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.IsNullOrWhiteSpace(field))
            return null;

        decimal? max = null;
        foreach (var row in rows)
        {
            if (row.TryGetValue(field, out var value) && value is not null && TryConvertToDecimal(value, out var decimalValue)
                && (max is null || decimalValue > max))
            {
                max = decimalValue;
            }
        }

        return max;
    }

    private static object? First(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => string.IsNullOrWhiteSpace(field) ? null : ValueAt(field, rows, fromEnd: false);

    private static object? Last(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => string.IsNullOrWhiteSpace(field) ? null : ValueAt(field, rows, fromEnd: true);

    private static object? ValueAt(string field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, bool fromEnd)
    {
        if (rows.Count == 0)
            return null;

        var index = fromEnd ? rows.Count - 1 : 0;
        var step = fromEnd ? -1 : 1;

        while (index >= 0 && index < rows.Count)
        {
            if (rows[index].TryGetValue(field, out var value) && value is not null)
                return value;
            index += step;
        }

        return null;
    }

    private static decimal? Median(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var values = NumericValues(field, rows);
        if (values.Count == 0)
            return null;

        var middle = values.Count / 2;
        return values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2m;
    }

    private static decimal? Percentile(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, double percentile)
    {
        var values = NumericValues(field, rows);
        if (values.Count == 0)
            return null;

        // Metodo do posto mais proximo (nearest-rank).
        var rank = (int)Math.Ceiling(percentile / 100d * values.Count);
        return values[Math.Clamp(rank - 1, 0, values.Count - 1)];
    }

    private static List<decimal> NumericValues(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var values = new List<decimal>();
        if (string.IsNullOrWhiteSpace(field))
            return values;

        foreach (var row in rows)
        {
            if (row.TryGetValue(field, out var value) && value is not null && TryConvertToDecimal(value, out var decimalValue))
                values.Add(decimalValue);
        }

        values.Sort();
        return values;
    }

    private static int CountIf(string? field, JsonElement? condition, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.IsNullOrWhiteSpace(field))
            return 0;

        if (condition is null)
            return rows.Count(row => row.TryGetValue(field, out var value) && value is bool boolValue && boolValue);

        return rows.Count(row => EvaluateCondition(row, field, condition.Value));
    }

    private static decimal SumIf(string? field, JsonElement? condition, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.IsNullOrWhiteSpace(field))
            return 0m;

        decimal sum = 0;
        foreach (var row in rows)
        {
            if (condition is null || !EvaluateCondition(row, field, condition.Value))
                continue;

            if (row.TryGetValue(field, out var value) && value is not null && TryConvertToDecimal(value, out var decimalValue))
                sum += decimalValue;
        }

        return sum;
    }

    private static decimal CompliancePercent(string? field, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var total = rows.Count;
        if (total == 0)
            return 0m;

        // Sem campo definido, considera conforme tudo o que nao for explicitamente false.
        var compliant = rows.Count(row =>
        {
            if (string.IsNullOrWhiteSpace(field) || !row.TryGetValue(field, out var value))
                return true;

            return value is not bool boolValue || !boolValue;
        });

        return Math.Round((decimal)compliant / total * 100, 1);
    }

    private static bool EvaluateCondition(IReadOnlyDictionary<string, object?> row, string field, JsonElement condition)
    {
        if (condition.ValueKind != JsonValueKind.Object)
            return false;

        if (condition.TryGetProperty("eq", out var equality))
        {
            row.TryGetValue(field, out var rowValue);
            var expected = equality.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.String => equality.GetString() ?? string.Empty,
                _ => equality.ToString()
            };

            var actual = rowValue switch
            {
                bool boolValue => boolValue.ToString().ToLowerInvariant(),
                _ => rowValue?.ToString() ?? string.Empty
            };

            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryConvertToDecimal(object value, out decimal decimalValue)
    {
        switch (value)
        {
            case decimal d: decimalValue = d; return true;
            case double d: decimalValue = Convert.ToDecimal(d); return true;
            case float f: decimalValue = Convert.ToDecimal(f); return true;
            case int i: decimalValue = i; return true;
            case long l: decimalValue = l; return true;
            case short s: decimalValue = s; return true;
            case byte b: decimalValue = b; return true;
            case string text when decimal.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                decimalValue = parsed; return true;
            default: decimalValue = 0; return false;
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Helpers;

/// <summary>
/// Aplica os campos calculados do layout as linhas do relatorio.
///
/// Fica em Core (e nao no ReportHtmlComposer) porque antes so o HTML calculava:
/// Markdown, XLSX e CSV exibiam a coluna calculada VAZIA.
///
/// O avaliador e intencionalmente simples (substituicao de campo + uma operacao
/// binaria + ternario). Expressoes com parenteses/precedencia precisam de um
/// parser real — ver roadmap no relatorio de montagem.
/// </summary>
public static class ReportComputedFieldEvaluator
{
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Enrich(
        ReportLayoutDefinition? layout,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (layout?.ComputedFields is not { Count: > 0 })
            return rows;

        var result = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var enriched = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
            foreach (var computed in layout.ComputedFields)
            {
                if (string.IsNullOrWhiteSpace(computed.Name) || string.IsNullOrWhiteSpace(computed.Expression))
                    continue;

                enriched[computed.Name] = Evaluate(computed.Expression, enriched);
            }

            result.Add(enriched);
        }

        return result;
    }

    private static object? Evaluate(string expression, IReadOnlyDictionary<string, object?> row)
    {
        try
        {
            // Avalia ternarios ANTES de substituir campos na string (evita corromper
            // condicoes como `status == 'Online'` ou `campo != null`).
            var ternary = Regex.Match(expression, @"^(.+?)\s*\?\s*(.+?)\s*:\s*(.+)$");
            if (ternary.Success)
            {
                var condition = ternary.Groups[1].Value.Trim();
                var whenTrue = ternary.Groups[2].Value.Trim();
                var whenFalse = ternary.Groups[3].Value.Trim();
                return EvaluateTernaryCondition(condition, row)
                    ? whenTrue.Trim('\'', '"')
                    : whenFalse.Trim('\'', '"');
            }

            // Substitui apenas identificadores completos (word boundaries) em ordem
            // decrescente de comprimento, preservando case-insensitivity.
            var resolvedExpression = expression;
            foreach (var key in row.Keys.OrderByDescending(key => key.Length))
            {
                if (!row.TryGetValue(key, out var value) || value is null)
                    continue;

                var pattern = $@"(?<![\w.]){Regex.Escape(key)}(?![\w.])";
                var replacement = FormatNumericLiteral(value);
                resolvedExpression = Regex.Replace(
                    resolvedExpression,
                    pattern,
                    _ => replacement,
                    RegexOptions.IgnoreCase);
            }

            var division = Regex.Match(resolvedExpression, @"^\s*(-?[0-9]+(?:\.[0-9]+)?)\s*/\s*(-?[0-9]+(?:\.[0-9]+)?)\s*$");
            if (division.Success
                && decimal.TryParse(division.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var dividend)
                && decimal.TryParse(division.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var divisor))
            {
                return divisor == 0 ? null : Math.Round(dividend / divisor, 2);
            }

            var subtraction = Regex.Match(resolvedExpression, @"^\s*(-?[0-9]+(?:\.[0-9]+)?)\s*-\s*(-?[0-9]+(?:\.[0-9]+)?)\s*$");
            if (subtraction.Success
                && decimal.TryParse(subtraction.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var minuend)
                && decimal.TryParse(subtraction.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var subtrahend))
            {
                return minuend - subtrahend;
            }

            var multiplication = Regex.Match(resolvedExpression, @"^\s*(-?[0-9]+(?:\.[0-9]+)?)\s*\*\s*(-?[0-9]+(?:\.[0-9]+)?)\s*$");
            if (multiplication.Success
                && decimal.TryParse(multiplication.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var left)
                && decimal.TryParse(multiplication.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var right))
            {
                return Math.Round(left * right, 2);
            }

            var addition = Regex.Match(resolvedExpression, @"^\s*(-?[0-9]+(?:\.[0-9]+)?)\s*\+\s*(-?[0-9]+(?:\.[0-9]+)?)\s*$");
            if (addition.Success
                && decimal.TryParse(addition.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var first)
                && decimal.TryParse(addition.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var second))
            {
                return first + second;
            }

            // Se ainda restar operador aritmetico com campo nulo/nao resolvido, nao vaza formula crua.
            if (Regex.IsMatch(resolvedExpression, @"[+\-*/]"))
                return null;

            return resolvedExpression;
        }
        catch
        {
            return null;
        }
    }

    private static bool EvaluateTernaryCondition(string condition, IReadOnlyDictionary<string, object?> row)
    {
        var notNull = Regex.Match(condition, @"([\w.]+)\s*!=\s*null", RegexOptions.IgnoreCase);
        if (notNull.Success)
        {
            var fieldName = notNull.Groups[1].Value;
            return row.TryGetValue(fieldName, out var value) && value is not null;
        }

        var isNull = Regex.Match(condition, @"([\w.]+)\s*==\s*null", RegexOptions.IgnoreCase);
        if (isNull.Success)
        {
            var fieldName = isNull.Groups[1].Value;
            return !row.TryGetValue(fieldName, out var value) || value is null;
        }

        var inequality = Regex.Match(condition, @"([\w.]+)\s*!=\s*(.+)");
        if (inequality.Success)
        {
            var fieldName = inequality.Groups[1].Value;
            var expected = inequality.Groups[2].Value.Trim().Trim('\'', '"');
            row.TryGetValue(fieldName, out var value);
            var actual = value switch
            {
                bool boolValue => boolValue.ToString().ToLowerInvariant(),
                _ => value?.ToString() ?? string.Empty
            };
            return !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        var equality = Regex.Match(condition, @"([\w.]+)\s*==\s*(.+)");
        if (equality.Success)
        {
            var fieldName = equality.Groups[1].Value;
            var expected = equality.Groups[2].Value.Trim().Trim('\'', '"');
            row.TryGetValue(fieldName, out var value);
            var actual = value switch
            {
                bool boolValue => boolValue.ToString().ToLowerInvariant(),
                _ => value?.ToString() ?? string.Empty
            };
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string FormatNumericLiteral(object value)
        => value switch
        {
            decimal decimalValue => decimalValue.ToString(CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString(CultureInfo.InvariantCulture),
            float floatValue => floatValue.ToString(CultureInfo.InvariantCulture),
            int intValue => intValue.ToString(CultureInfo.InvariantCulture),
            long longValue => longValue.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "0"
        };
}

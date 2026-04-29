using System.Text;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Markdown report renderer producing GitHub Flavored Markdown (GFM).
/// Generates .md files with tables, grouped sections, summary cards, and multi-section support.
/// Useful for wiki export, knowledge base integration, and AI/LLM consumption.
/// </summary>
public class MarkdownReportRenderer : IReportRenderer
{
    public ReportFormat Format => ReportFormat.Markdown;

    public Task<ReportDocument> RenderAsync(ReportRenderContext context, ReportQueryResult data, CancellationToken cancellationToken = default)
    {
        var layout = ReportLayoutDefinitionParser.ParseOrDefault(context.LayoutJson);
        var columns = ResolveColumns(layout, data);
        var sb = new StringBuilder();

        // Header
        sb.AppendLine($"# {EscapeHeading(context.Title)}");
        if (!string.IsNullOrWhiteSpace(context.Subtitle))
        {
            sb.AppendLine();
            sb.AppendLine($"_{EscapeHeading(context.Subtitle)}_");
        }
        sb.AppendLine();

        // Global summaries
        if (layout.Summaries is { Count: > 0 })
        {
            sb.AppendLine("## 📊 Resumo");
            sb.AppendLine();
            sb.Append(BuildSummaryTable(layout.Summaries, data.Rows));
            sb.AppendLine();
        }

        // Charts placeholder (charts are rendered as images in PDF; markdown gets alt-text placeholders)
        if (layout.Charts is { Count: > 0 })
        {
            sb.AppendLine("## 📈 Gráficos");
            sb.AppendLine();
            foreach (var chart in layout.Charts)
            {
                var chartTitle = string.IsNullOrWhiteSpace(chart.Title) ? chart.Type ?? "Chart" : chart.Title;
                sb.AppendLine($"![{EscapeHeading(chartTitle)}]({EscapeHeading(chartTitle)} \"{chartTitle}\")");
                sb.AppendLine();
            }
        }

        // Content: grouped or ungrouped
        if (!string.IsNullOrWhiteSpace(layout.GroupBy))
        {
            BuildGroupedContent(sb, layout, columns, data.Rows);
        }
        else
        {
            BuildUngroupedContent(sb, layout, columns, data.Rows);
        }

        // Footer
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"_Gerado por Discovery RMM em {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC_");

        return Task.FromResult(new ReportDocument
        {
            Content = Encoding.UTF8.GetBytes(sb.ToString()),
            ContentType = "text/markdown; charset=utf-8",
            FileExtension = "md"
        });
    }

    private static void BuildGroupedContent(StringBuilder sb, ReportLayoutDefinition layout, IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var grouped = rows
            .GroupBy(row => GetGroupValue(row, layout.GroupBy!))
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var groupRows = group.ToList();
            var title = BuildGroupTitle(layout, group.Key, groupRows.Count);

            sb.AppendLine($"## {EscapeHeading(title)}");

            // Group details as bullet list
            if (layout.GroupDetails is { Count: > 0 } && groupRows.FirstOrDefault() is { } firstRow)
            {
                sb.AppendLine();
                foreach (var detail in layout.GroupDetails)
                {
                    if (string.IsNullOrWhiteSpace(detail.Field))
                        continue;

                    firstRow.TryGetValue(detail.Field, out var value);
                    var label = string.IsNullOrWhiteSpace(detail.Label) ? detail.Field : detail.Label;
                    var formattedValue = FormatValue(value, detail.Format);
                    sb.AppendLine($"- **{EscapeHeading(label)}**: {EscapeHeading(formattedValue)}");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"_Registros: {groupRows.Count}_");
            sb.AppendLine();

            // Group summaries
            if (layout.GroupSummaries is { Count: > 0 })
            {
                sb.Append(BuildSummaryTable(layout.GroupSummaries, groupRows));
                sb.AppendLine();
            }

            // Content tables
            if (layout.Sections is { Count: > 0 })
            {
                foreach (var section in layout.Sections)
                {
                    var sectionColumns = (section.Columns ?? [])
                        .Where(c => !string.IsNullOrWhiteSpace(c.Field))
                        .Select(c => new ReportLayoutColumn(c.Field!, ResolveDisplayHeader(c), c.Format, section.Title))
                        .ToList();

                    if (sectionColumns.Count == 0) continue;

                    if (!string.IsNullOrWhiteSpace(section.Title))
                        sb.AppendLine($"### {EscapeHeading(section.Title)}");

                    BuildMarkdownTable(sb, sectionColumns, groupRows);
                }
            }
            else
            {
                var filteredColumns = FilterColumnsForGrouping(columns, layout);
                BuildMarkdownTable(sb, filteredColumns, groupRows);
            }

            sb.AppendLine();
        }
    }

    private static void BuildUngroupedContent(StringBuilder sb, ReportLayoutDefinition layout, IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (layout.Sections is { Count: > 0 })
        {
            foreach (var section in layout.Sections)
            {
                var sectionColumns = (section.Columns ?? [])
                    .Where(c => !string.IsNullOrWhiteSpace(c.Field))
                    .Select(c => new ReportLayoutColumn(c.Field!, ResolveDisplayHeader(c), c.Format, section.Title))
                    .ToList();

                if (sectionColumns.Count == 0) continue;

                if (!string.IsNullOrWhiteSpace(section.Title))
                    sb.AppendLine($"## {EscapeHeading(section.Title)}");

                BuildMarkdownTable(sb, sectionColumns, rows);
            }
        }
        else
        {
            sb.AppendLine("## Dados");
            sb.AppendLine();
            BuildMarkdownTable(sb, columns, rows);
        }
    }

    private static void BuildMarkdownTable(StringBuilder sb, IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        // Header row
        var headers = columns.Select(c => EscapePipe(c.Header)).ToList();
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");

        // Separator row
        var separators = columns.Select(_ => "---").ToList();
        sb.AppendLine("| " + string.Join(" | ", separators) + " |");

        // Data rows
        foreach (var row in rows)
        {
            var cells = columns.Select(c =>
            {
                row.TryGetValue(c.Field, out var value);
                return EscapePipe(FormatValue(value, c.Format));
            });
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }
    }

    private static string BuildSummaryTable(IReadOnlyList<ReportLayoutSummaryDefinition> summaries, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();

        sb.AppendLine("| Métrica | Valor |");
        sb.AppendLine("| --- | --- |");

        foreach (var summary in summaries)
        {
            var label = string.IsNullOrWhiteSpace(summary.Label) ? summary.Aggregate ?? "Métrica" : summary.Label;
            var value = ComputeSummaryValue(summary, rows);
            var formattedValue = value is null ? "-" : FormatValue(value, summary.Format);
            sb.AppendLine($"| {EscapePipe(label)} | {EscapePipe(formattedValue)} |");
        }

        return sb.ToString();
    }

    private static string BuildSummaryCards(IReadOnlyList<ReportLayoutSummaryDefinition> summaries, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var summary in summaries)
        {
            var label = string.IsNullOrWhiteSpace(summary.Label) ? summary.Aggregate ?? "Métrica" : summary.Label;
            var value = ComputeSummaryValue(summary, rows);
            var formattedValue = value is null ? "-" : FormatValue(value, summary.Format);
            sb.AppendLine($"- **{EscapeHeading(label)}**: {EscapeHeading(formattedValue)}");
        }
        return sb.ToString();
    }

    private static object? ComputeSummaryValue(ReportLayoutSummaryDefinition summary, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.Equals(summary.Aggregate, "count", StringComparison.OrdinalIgnoreCase))
            return rows.Count;

        if (string.IsNullOrWhiteSpace(summary.Field))
            return null;

        if (string.Equals(summary.Aggregate, "countDistinct", StringComparison.OrdinalIgnoreCase))
        {
            return rows
                .Where(r => r.TryGetValue(summary.Field, out var v) && v is not null)
                .Select(r => r[summary.Field]?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        if (string.Equals(summary.Aggregate, "sum", StringComparison.OrdinalIgnoreCase))
        {
            decimal sum = 0;
            foreach (var row in rows)
            {
                if (!row.TryGetValue(summary.Field, out var v) || v is null) continue;
                if (TryConvertToDecimal(v, out var d)) sum += d;
            }
            return sum;
        }

        if (string.Equals(summary.Aggregate, "avg", StringComparison.OrdinalIgnoreCase))
        {
            decimal sum = 0;
            int count = 0;
            foreach (var row in rows)
            {
                if (!row.TryGetValue(summary.Field, out var v) || v is null) continue;
                if (TryConvertToDecimal(v, out var d)) { sum += d; count++; }
            }
            return count > 0 ? sum / count : null;
        }

        if (string.Equals(summary.Aggregate, "min", StringComparison.OrdinalIgnoreCase))
        {
            decimal? min = null;
            foreach (var row in rows)
            {
                if (!row.TryGetValue(summary.Field, out var v) || v is null) continue;
                if (TryConvertToDecimal(v, out var d) && (min is null || d < min)) min = d;
            }
            return min;
        }

        if (string.Equals(summary.Aggregate, "max", StringComparison.OrdinalIgnoreCase))
        {
            decimal? max = null;
            foreach (var row in rows)
            {
                if (!row.TryGetValue(summary.Field, out var v) || v is null) continue;
                if (TryConvertToDecimal(v, out var d) && (max is null || d > max)) max = d;
            }
            return max;
        }

        return null;
    }

    private static IReadOnlyList<ReportLayoutColumn> ResolveColumns(ReportLayoutDefinition layout, ReportQueryResult data)
    {
        if (layout.Columns is { Count: > 0 })
        {
            var direct = layout.Columns
                .Where(c => !string.IsNullOrWhiteSpace(c.Field))
                .Select(c => new ReportLayoutColumn(c.Field!, ResolveDisplayHeader(c), c.Format, null))
                .ToList();
            if (direct.Count > 0) return direct;
        }

        return data.Columns.Select(c => new ReportLayoutColumn(c, c, null, null)).ToList();
    }

    private static IReadOnlyList<ReportLayoutColumn> FilterColumnsForGrouping(IReadOnlyList<ReportLayoutColumn> columns, ReportLayoutDefinition layout)
    {
        if (!layout.HideGroupColumn || string.IsNullOrWhiteSpace(layout.GroupBy)) return columns;
        var filtered = columns.Where(c => !string.Equals(c.Field, layout.GroupBy, StringComparison.OrdinalIgnoreCase)).ToList();
        return filtered.Count == 0 ? columns : filtered;
    }

    private static string? GetGroupValue(IReadOnlyDictionary<string, object?> row, string groupBy)
    {
        if (row.TryGetValue(groupBy, out var value) && value is not null)
            return value.ToString();

        // Try alias.field pattern
        var candidate = row.Keys.FirstOrDefault(k => k.EndsWith($".{groupBy}", StringComparison.OrdinalIgnoreCase));
        if (candidate is not null && row.TryGetValue(candidate, out var dotted) && dotted is not null)
            return dotted.ToString();

        return "(sem valor)";
    }

    private static string BuildGroupTitle(ReportLayoutDefinition layout, string? groupValue, int count)
    {
        if (!string.IsNullOrWhiteSpace(layout.GroupTitleTemplate))
        {
            return layout.GroupTitleTemplate.Replace("{{value}}", groupValue ?? "");
        }

        var prefix = string.IsNullOrWhiteSpace(layout.GroupTitlePrefix) ? "" : layout.GroupTitlePrefix + " ";
        return $"{prefix}{groupValue ?? "Grupo"} ({count} registro(s))";
    }

    private static string ResolveDisplayHeader(ReportLayoutColumnDefinition column)
    {
        var header = string.IsNullOrWhiteSpace(column.DisplayHeader) ? column.Field : column.DisplayHeader;
        return string.IsNullOrWhiteSpace(header) ? string.Empty : header;
    }

    private static string FormatValue(object? value, string? format)
    {
        if (value is null) return "-";

        if (string.Equals(format, "bytes", StringComparison.OrdinalIgnoreCase) && TryConvertToDecimal(value, out var bytes))
            return FormatBytes(bytes);

        if (string.Equals(format, "datetime", StringComparison.OrdinalIgnoreCase) && value is DateTime dt)
            return dt.ToString("yyyy-MM-dd HH:mm:ss");

        if (string.Equals(format, "number", StringComparison.OrdinalIgnoreCase) && TryConvertToDecimal(value, out var num))
            return num.ToString("N2");

        if (string.Equals(format, "percent", StringComparison.OrdinalIgnoreCase) && TryConvertToDecimal(value, out var pct))
            return $"{pct:F1}%";

        return value.ToString() ?? "-";
    }

    private static string FormatBytes(decimal bytes)
    {
        if (bytes >= 1_099_511_627_776m) return $"{bytes / 1_099_511_627_776m:F1} TB";
        if (bytes >= 1_073_741_824m) return $"{bytes / 1_073_741_824m:F1} GB";
        if (bytes >= 1_048_576m) return $"{bytes / 1_048_576m:F1} MB";
        if (bytes >= 1_024m) return $"{bytes / 1_024m:F1} KB";
        return $"{bytes:F0} B";
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
            case string s when decimal.TryParse(s, out var p): decimalValue = p; return true;
            default: decimalValue = 0; return false;
        }
    }

    private static string EscapeHeading(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Replace("\n", " ").Replace("\r", "").Replace("|", "\\|");
    }

    private static string EscapePipe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
    }

    private sealed record ReportLayoutColumn(string Field, string Header, string? Format, string? SectionTitle);
}

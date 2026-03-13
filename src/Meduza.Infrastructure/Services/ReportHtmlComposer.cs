using System.Text;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;

namespace Meduza.Infrastructure.Services;

public class ReportHtmlComposer : IReportHtmlComposer
{
    public string Compose(ReportRenderContext context, ReportQueryResult data)
    {
        var layout = ReportLayoutDefinitionParser.ParseOrDefault(context.LayoutJson);
        var columns = ResolveColumns(layout, data);
        var logoUrl = layout.LogoUrl ?? layout.Style?.LogoUrl;
        var style = layout.Style ?? new ReportLayoutStyleDefinition();
        var content = string.IsNullOrWhiteSpace(layout.GroupBy)
            ? BuildUngroupedContent(layout, columns, data.Rows)
            : BuildGroupedSections(layout, columns, data.Rows);

        var subtitleHtml = string.IsNullOrWhiteSpace(context.Subtitle)
            ? string.Empty
            : $"<p class=\"report-subtitle\">{HtmlEscape(context.Subtitle)}</p>";

        var logoHtml = string.IsNullOrWhiteSpace(logoUrl)
            ? string.Empty
            : $"<img class=\"report-logo\" src=\"{HtmlAttributeEscape(logoUrl)}\" alt=\"logo\" />";

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <style>
                    :root {
                        --report-primary: {{CssValueOrDefault(style.PrimaryColor, "#0f4c81")}};
                        --report-header-bg: {{CssValueOrDefault(style.HeaderBackgroundColor, style.PrimaryColor, "#0f4c81")}};
                        --report-header-text: {{CssValueOrDefault(style.HeaderTextColor, "#ffffff")}};
                        --report-alt-row: {{CssValueOrDefault(style.AlternateRowColor, "#f5f7fb")}};
                        --report-border: {{CssValueOrDefault(style.BorderColor, "#d9e2ec")}};
                        --report-muted: {{CssValueOrDefault(style.SecondaryColor, "#52606d")}};
                        --report-font: {{CssValueOrDefault(style.FontFamily, "Arial, sans-serif")}};
                    }

                    body { font-family: var(--report-font); margin: 0; color: #1f2933; background: #ffffff; }
                    .report-shell { padding: 12px 6px; }
                    .report-header { display:flex; justify-content:space-between; align-items:flex-start; gap:20px; border-bottom:3px solid var(--report-primary); padding-bottom:14px; margin-bottom:18px; }
                    .report-title { margin:0; color:var(--report-primary); font-size:26px; }
                    .report-subtitle { margin:6px 0 0; color:var(--report-muted); font-size:13px; }
                    .report-logo { max-height: {{Math.Clamp(style.LogoMaxHeightPx ?? 56, 24, 180)}}px; max-width:220px; object-fit:contain; }
                    .report-group { margin: 18px 0 24px; page-break-inside: avoid; }
                    .report-group-title { margin: 0 0 8px; font-size: 18px; color: var(--report-primary); }
                    .report-group-meta { margin: 0 0 10px; color: var(--report-muted); font-size: 12px; }
                    .details-grid { display:grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 10px; margin: 12px 0 14px; }
                    .detail-card { padding: 10px 12px; border:1px solid var(--report-border); border-radius:10px; background:#fff; }
                    .detail-label { font-size:11px; color:var(--report-muted); text-transform:uppercase; letter-spacing:0.04em; }
                    .detail-value { margin-top:6px; font-size:14px; font-weight:600; }
                    table { width:100%; border-collapse:collapse; table-layout:fixed; margin-top:10px; }
                    th, td { border:1px solid var(--report-border); padding:8px 10px; font-size:12px; text-align:left; vertical-align:top; word-wrap:break-word; }
                    th { background:var(--report-header-bg); color:var(--report-header-text); font-weight:700; }
                    tbody tr:nth-child(even) { background:var(--report-alt-row); }
                    .section-caption { margin: 16px 0 6px; color: var(--report-muted); font-size:11px; font-weight:700; text-transform:uppercase; letter-spacing:0.04em; }
                </style>
            </head>
            <body>
                <div class="report-shell">
                    <div class="report-header">
                        <div>
                            <h1 class="report-title">{{HtmlEscape(context.Title)}}</h1>
                            {{subtitleHtml}}
                        </div>
                        {{logoHtml}}
                    </div>
                    {{content}}
                </div>
            </body>
            </html>
            """;
    }

    private static string BuildGroupedSections(ReportLayoutDefinition layout, IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var grouped = rows.GroupBy(row => GetGroupValue(row, layout.GroupBy!)).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var group in grouped)
        {
            var groupRows = group.ToList();
            var title = BuildGroupTitle(layout, group.Key, groupRows.Count);
            builder.Append("<section class=\"report-group\">");
            builder.Append($"<h2 class=\"report-group-title\">{HtmlEscape(title)}</h2>");
            builder.Append($"<p class=\"report-group-meta\">{groupRows.Count} registro(s)</p>");
            builder.Append(BuildDetailsGrid(layout.GroupDetails, groupRows.FirstOrDefault()));
            if (layout.GroupSummaries is { Count: > 0 })
                builder.Append(BuildSummaryCards(layout.GroupSummaries, groupRows));
            builder.Append(layout.Sections is { Count: > 0 } ? BuildSectionTables(layout, groupRows) : BuildSingleTable(FilterColumnsForGrouping(columns, layout), groupRows));
            builder.Append("</section>");
        }

        return builder.ToString();
    }

    private static string BuildUngroupedContent(ReportLayoutDefinition layout, IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var builder = new StringBuilder();
        if (layout.Summaries is { Count: > 0 })
            builder.Append(BuildSummaryCards(layout.Summaries, rows));
        builder.Append(layout.Sections is { Count: > 0 } ? BuildSectionTables(layout, rows) : BuildSingleTable(columns, rows));
        return builder.ToString();
    }

    private static string BuildSectionTables(ReportLayoutDefinition layout, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (layout.Sections is not { Count: > 0 })
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var section in layout.Sections)
        {
            var columns = (section.Columns ?? [])
                .Where(column => !string.IsNullOrWhiteSpace(column.Field))
                .Select(column => new ReportLayoutColumn(column.Field!, ResolveDisplayHeader(column), column.Format, section.Title))
                .ToList();

            if (columns.Count == 0)
                continue;

            builder.Append(BuildSingleTable(columns, rows));
        }

        return builder.ToString();
    }

    private static string BuildDetailsGrid(IReadOnlyList<ReportLayoutColumnDefinition>? details, IReadOnlyDictionary<string, object?>? row)
    {
        if (details is not { Count: > 0 } || row is null)
            return string.Empty;

        var cards = details
            .Where(detail => !string.IsNullOrWhiteSpace(detail.Field))
            .Select(detail =>
            {
                row.TryGetValue(detail.Field!, out var value);
                var header = ResolveDisplayHeader(detail);
                return $$"""
                    <div class="detail-card">
                        <div class="detail-label">{{HtmlEscape(header)}}</div>
                        <div class="detail-value">{{HtmlEscape(FormatValue(value, detail.Format))}}</div>
                    </div>
                    """;
            })
            .ToList();

        if (cards.Count == 0)
            return string.Empty;

        return $$"""
            <div class="details-grid">
                {{string.Join(string.Empty, cards)}}
            </div>
            """;
    }

    private static string BuildSingleTable(IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var headers = string.Join(string.Empty, columns.Select(column => $"<th>{HtmlEscape(column.Header)}</th>"));
        var caption = columns.Select(column => column.SectionTitle).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
        var captionHtml = string.IsNullOrWhiteSpace(caption) ? string.Empty : $"<div class=\"section-caption\">{HtmlEscape(caption)}</div>";
        var rowsHtml = string.Join("\n", rows.Select(row =>
        {
            var cells = columns.Select(column =>
            {
                row.TryGetValue(column.Field, out var value);
                return $"<td>{HtmlEscape(FormatValue(value, column.Format))}</td>";
            });
            return $"<tr>{string.Join(string.Empty, cells)}</tr>";
        }));

        return $$"""
            {{captionHtml}}
            <table>
                <thead>
                    <tr>{{headers}}</tr>
                </thead>
                <tbody>
                    {{rowsHtml}}
                </tbody>
            </table>
            """;
    }

    private static IReadOnlyList<ReportLayoutColumn> ResolveColumns(ReportLayoutDefinition layout, ReportQueryResult data)
    {
        if (layout.Columns is { Count: > 0 })
        {
            var directColumns = layout.Columns
                .Where(column => !string.IsNullOrWhiteSpace(column.Field))
                .Select(column => new ReportLayoutColumn(column.Field!, ResolveDisplayHeader(column), column.Format, null))
                .ToList();

            if (directColumns.Count > 0)
                return directColumns;
        }

        return data.Columns.Select(column => new ReportLayoutColumn(column, column, null, null)).ToList();
    }

    private static string BuildSummaryCards(IReadOnlyList<ReportLayoutSummaryDefinition> summaries, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var items = summaries.Select(summary => BuildSummaryCard(summary, rows)).Where(item => item is not null).Cast<string>().ToList();
        if (items.Count == 0)
            return string.Empty;

        return $$"""
            <div style="display:flex;gap:12px;flex-wrap:wrap;margin:10px 0 16px;">
                {{string.Join(string.Empty, items)}}
            </div>
            """;
    }

    private static string? BuildSummaryCard(ReportLayoutSummaryDefinition summary, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var label = string.IsNullOrWhiteSpace(summary.Label) ? summary.Aggregate ?? "Summary" : summary.Label;
        var value = ComputeSummaryValue(summary, rows);
        if (value is null)
            return null;

        return $$"""
            <div style="min-width:140px;padding:10px 12px;border:1px solid var(--report-border);border-radius:10px;background:#fff;">
                <div style="font-size:11px;color:var(--report-muted);text-transform:uppercase;letter-spacing:0.04em;">{{HtmlEscape(label)}}</div>
                <div style="margin-top:6px;font-size:20px;font-weight:700;color:var(--report-primary);">{{HtmlEscape(FormatValue(value, summary.Format))}}</div>
            </div>
            """;
    }

    private static object? ComputeSummaryValue(ReportLayoutSummaryDefinition summary, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.Equals(summary.Aggregate, "count", StringComparison.OrdinalIgnoreCase))
            return rows.Count;
        if (string.IsNullOrWhiteSpace(summary.Field))
            return null;

        if (string.Equals(summary.Aggregate, "countDistinct", StringComparison.OrdinalIgnoreCase))
        {
            return rows.Where(row => row.TryGetValue(summary.Field, out var value) && value is not null)
                .Select(row => row[summary.Field]?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        if (string.Equals(summary.Aggregate, "sum", StringComparison.OrdinalIgnoreCase))
        {
            decimal sum = 0;
            foreach (var row in rows)
            {
                if (!row.TryGetValue(summary.Field, out var value) || value is null)
                    continue;
                if (TryConvertToDecimal(value, out var decimalValue))
                    sum += decimalValue;
            }
            return sum;
        }

        return null;
    }

    private static IReadOnlyList<ReportLayoutColumn> FilterColumnsForGrouping(IReadOnlyList<ReportLayoutColumn> columns, ReportLayoutDefinition layout)
    {
        if (!layout.HideGroupColumn || string.IsNullOrWhiteSpace(layout.GroupBy))
            return columns;
        var filtered = columns.Where(column => !string.Equals(column.Field, layout.GroupBy, StringComparison.OrdinalIgnoreCase)).ToList();
        return filtered.Count == 0 ? columns : filtered;
    }

    private static bool TryConvertToDecimal(object value, out decimal decimalValue)
    {
        switch (value)
        {
            case decimal currentDecimal:
                decimalValue = currentDecimal;
                return true;
            case double currentDouble:
                decimalValue = Convert.ToDecimal(currentDouble);
                return true;
            case float currentFloat:
                decimalValue = Convert.ToDecimal(currentFloat);
                return true;
            case int currentInt:
                decimalValue = currentInt;
                return true;
            case long currentLong:
                decimalValue = currentLong;
                return true;
            case string currentString when decimal.TryParse(currentString, out var parsed):
                decimalValue = parsed;
                return true;
            default:
                decimalValue = 0;
                return false;
        }
    }

    private static string FormatValue(object? value, string? format)
    {
        if (value is null)
            return string.Empty;
        if (value is DateTime dateTime)
            return string.Equals(format, "datetime", StringComparison.OrdinalIgnoreCase) ? dateTime.ToString("yyyy-MM-dd HH:mm:ss") : dateTime.ToString("yyyy-MM-dd");
        if (value is DateTimeOffset dateTimeOffset)
            return string.Equals(format, "datetime", StringComparison.OrdinalIgnoreCase) ? dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss zzz") : dateTimeOffset.ToString("yyyy-MM-dd");
        if (value is decimal decimalValue && string.Equals(format, "number", StringComparison.OrdinalIgnoreCase))
            return decimalValue.ToString("0.##");
        if (value is double doubleValue && string.Equals(format, "number", StringComparison.OrdinalIgnoreCase))
            return doubleValue.ToString("0.##");
        return value.ToString() ?? string.Empty;
    }

    private static string BuildGroupTitle(ReportLayoutDefinition layout, string? key, int count)
    {
        var value = string.IsNullOrWhiteSpace(key) ? "Nao informado" : key;
        if (!string.IsNullOrWhiteSpace(layout.GroupTitleTemplate))
        {
            return layout.GroupTitleTemplate!.Replace("{value}", value, StringComparison.OrdinalIgnoreCase)
                .Replace("{count}", count.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return string.IsNullOrWhiteSpace(layout.GroupTitlePrefix) ? value : $"{layout.GroupTitlePrefix} {value}";
    }

    private static string? GetGroupValue(IReadOnlyDictionary<string, object?> row, string groupBy)
        => row.TryGetValue(groupBy, out var value) ? value?.ToString() : null;

    private static string CssValueOrDefault(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static string HtmlAttributeEscape(string? text) => System.Net.WebUtility.HtmlEncode(text ?? string.Empty);
    private static string HtmlEscape(string? text) => System.Net.WebUtility.HtmlEncode(text ?? string.Empty);

    private static string ResolveDisplayHeader(ReportLayoutColumnDefinition column)
    {
        var display = column.DisplayHeader;
        return string.IsNullOrWhiteSpace(display) ? (column.Field ?? string.Empty) : display;
    }

    private sealed record ReportLayoutColumn(string Field, string Header, string? Format, string? SectionTitle);
}
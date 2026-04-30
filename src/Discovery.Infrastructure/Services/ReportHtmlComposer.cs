using System.Text;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

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

        // Cover page (before the shell)
        var coverPageHtml = BuildCoverPage(layout, context, data.Rows.Count, logoUrl, style);

        // Table of Contents
        var tocHtml = BuildTableOfContents(layout);

        // Charts
        var chartsHtml = BuildChartsSection(layout);

        // Page header/footer
        var pageHeaderHtml = BuildPageHeader(layout);
        var pageFooterHtml = BuildPageFooter(layout);

        // Watermark
        var watermarkHtml = BuildWatermark(layout);

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

                    @page {
                        size: A4 {{layout.Orientation ?? "portrait"}};
                        margin: 20mm 15mm 25mm 15mm;
                        @top-center { content: "{{pageHeaderHtml}}"; font-size: 10px; color: var(--report-muted); }
                        @bottom-center { content: "{{pageFooterHtml}}"; font-size: 9px; color: var(--report-muted); }
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

                    /* Cover page */
                    .report-cover { page-break-after: always; display:flex; flex-direction:column; justify-content:center; align-items:center; min-height:90vh; text-align:center; }
                    .report-cover-title { font-size:36px; color:var(--report-primary); margin-bottom:16px; }
                    .report-cover-subtitle { font-size:16px; color:var(--report-muted); margin-bottom:40px; }
                    .report-cover-meta { font-size:12px; color:var(--report-muted); line-height:1.8; }
                    .report-cover-logo { max-height:80px; max-width:280px; margin-bottom:30px; }

                    /* TOC */
                    .report-toc { page-break-after: always; }
                    .report-toc-title { font-size:22px; color:var(--report-primary); border-bottom:2px solid var(--report-primary); padding-bottom:8px; margin-bottom:16px; }
                    .report-toc-item { display:flex; justify-content:space-between; padding:6px 0; font-size:13px; }
                    .report-toc-item-level1 { font-weight:700; }
                    .report-toc-item-level2 { padding-left:20px; }

                    /* Charts */
                    .report-charts { margin: 20px 0; page-break-inside: avoid; }
                    .report-chart { margin: 16px 0; text-align:center; }
                    .report-chart-title { font-size:14px; font-weight:700; margin-bottom:8px; color:var(--report-primary); }
                    .report-chart img { max-width:100%; height:auto; }

                    /* Watermark */
                    .report-watermark { position:fixed; top:0; left:0; width:100%; height:100%; pointer-events:none; z-index:-1; opacity:0.06; display:flex; align-items:center; justify-content:center; font-size:{{layout.Watermark?.FontSize ?? 120}}px; color:{{CssValueOrDefault(layout.Watermark?.Color, "#000000")}}; transform:rotate({{layout.Watermark?.Angle ?? -45}}deg); }
                </style>
            </head>
            <body>
                {{coverPageHtml}}
                {{tocHtml}}
                {{watermarkHtml}}
                <div class="report-shell">
                    <div class="report-header">
                        <div>
                            <h1 class="report-title">{{HtmlEscape(context.Title)}}</h1>
                            {{subtitleHtml}}
                        </div>
                        {{logoHtml}}
                    </div>
                    {{chartsHtml}}
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
                .Select(column => new ReportLayoutColumn(column.Field!, ResolveDisplayHeader(column), column.Format, section.Title, column.ConditionalFormat))
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
                .Select(column => new ReportLayoutColumn(column.Field!, ResolveDisplayHeader(column), column.Format, null, column.ConditionalFormat))
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

        if (string.Equals(summary.Aggregate, "avg", StringComparison.OrdinalIgnoreCase))
        {
            decimal sum = 0;
            int count = 0;
            foreach (var row in rows)
            {
                if (!row.TryGetValue(summary.Field, out var value) || value is null)
                    continue;
                if (TryConvertToDecimal(value, out var decimalValue)) { sum += decimalValue; count++; }
            }
            return count > 0 ? sum / count : null;
        }

        if (string.Equals(summary.Aggregate, "min", StringComparison.OrdinalIgnoreCase))
        {
            decimal? min = null;
            foreach (var row in rows)
            {
                if (!row.TryGetValue(summary.Field, out var value) || value is null)
                    continue;
                if (TryConvertToDecimal(value, out var decimalValue) && (min is null || decimalValue < min))
                    min = decimalValue;
            }
            return min;
        }

        if (string.Equals(summary.Aggregate, "max", StringComparison.OrdinalIgnoreCase))
        {
            decimal? max = null;
            foreach (var row in rows)
            {
                if (!row.TryGetValue(summary.Field, out var value) || value is null)
                    continue;
                if (TryConvertToDecimal(value, out var decimalValue) && (max is null || decimalValue > max))
                    max = decimalValue;
            }
            return max;
        }

        return null;
    }

    private static string? ResolveConditionalCellStyle(ReportLayoutConditionalFormat? conditionalFormat, object? value)
    {
        if (conditionalFormat?.Rules is not { Count: > 0 })
            return null;

        foreach (var rule in conditionalFormat.Rules)
        {
            if (EvaluateCondition(rule.Operator, value, rule.Value))
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(rule.BackgroundColor))
                    parts.Add($"background-color:{rule.BackgroundColor}");
                if (!string.IsNullOrWhiteSpace(rule.TextColor))
                    parts.Add($"color:{rule.TextColor}");
                return parts.Count > 0 ? string.Join(";", parts) : null;
            }
        }

        return null;
    }

    private static string? ResolveConditionalIcon(ReportLayoutConditionalFormat? conditionalFormat, object? value)
    {
        if (conditionalFormat?.Rules is not { Count: > 0 })
            return null;

        foreach (var rule in conditionalFormat.Rules)
        {
            if (EvaluateCondition(rule.Operator, value, rule.Value) && !string.IsNullOrWhiteSpace(rule.Icon))
                return rule.Icon;
        }

        return null;
    }

    private static bool EvaluateCondition(string? op, object? left, object? right)
    {
        if (op is null || left is null || right is null) return false;

        if (string.Equals(op, "eq", StringComparison.OrdinalIgnoreCase))
            return string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);

        if (TryConvertToDecimal(left, out var leftNum) && TryConvertToDecimal(right, out var rightNum))
        {
            return op.ToLowerInvariant() switch
            {
                "lt" => leftNum < rightNum,
                "lte" => leftNum <= rightNum,
                "gt" => leftNum > rightNum,
                "gte" => leftNum >= rightNum,
                _ => false
            };
        }

        return false;
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

        if (string.Equals(format, "bytes", StringComparison.OrdinalIgnoreCase) && TryConvertToDecimal(value, out var bytes))
            return FormatBytes(bytes);

        if (string.Equals(format, "percent", StringComparison.OrdinalIgnoreCase) && TryConvertToDecimal(value, out var pct))
            return $"{pct:F1}%";

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

    private static string FormatBytes(decimal bytes)
    {
        if (bytes >= 1_099_511_627_776m) return $"{bytes / 1_099_511_627_776m:F1} TB";
        if (bytes >= 1_073_741_824m) return $"{bytes / 1_073_741_824m:F1} GB";
        if (bytes >= 1_048_576m) return $"{bytes / 1_048_576m:F1} MB";
        if (bytes >= 1_024m) return $"{bytes / 1_024m:F1} KB";
        return $"{bytes:F0} B";
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

    // ─── Cover Page ────────────────────────────────────────────────

    private static string BuildCoverPage(ReportLayoutDefinition layout, ReportRenderContext context, int rowCount, string? logoUrl, ReportLayoutStyleDefinition style)
    {
        if (layout.CoverPage is not { Enabled: true })
            return string.Empty;

        var title = string.IsNullOrWhiteSpace(layout.CoverPage.Title) ? context.Title : layout.CoverPage.Title;
        var subtitle = layout.CoverPage.Subtitle ?? context.Subtitle ?? "";
        var logoHtml = string.IsNullOrWhiteSpace(layout.CoverPage.LogoUrl ?? logoUrl)
            ? string.Empty
            : $"<img class=\"report-cover-logo\" src=\"{HtmlAttributeEscape(layout.CoverPage.LogoUrl ?? logoUrl!)}\" alt=\"logo\" />";

        var meta = new StringBuilder();
        if (layout.CoverPage.ShowGeneratedAt)
            meta.AppendLine($"<div>Gerado em: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</div>");
        if (layout.CoverPage.ShowRowCount)
            meta.AppendLine($"<div>Registros: {rowCount}</div>");
        if (layout.CoverPage.ShowParameters && !string.IsNullOrWhiteSpace(context.LayoutJson))
            meta.AppendLine("<div>Fonte: Discovery RMM Reporting Engine</div>");

        return $$"""
            <div class="report-cover">
                {{logoHtml}}
                <h1 class="report-cover-title">{{HtmlEscape(title)}}</h1>
                <p class="report-cover-subtitle">{{HtmlEscape(subtitle)}}</p>
                <div class="report-cover-meta">
                    {{meta}}
                </div>
            </div>
            """;
    }

    // ─── Table of Contents ─────────────────────────────────────────

    private static string BuildTableOfContents(ReportLayoutDefinition layout)
    {
        if (layout.TableOfContents is not { Enabled: true })
            return string.Empty;

        var tocTitle = string.IsNullOrWhiteSpace(layout.TableOfContents.Title) ? "Índice" : layout.TableOfContents.Title;

        // Build TOC from group titles
        var items = new List<(string Title, int Level)>();
        items.Add((layout.Title ?? "Relatório", 1));

        if (layout.Sections is { Count: > 0 })
        {
            foreach (var section in layout.Sections)
            {
                if (!string.IsNullOrWhiteSpace(section.Title))
                    items.Add((section.Title, 2));
            }
        }

        var tocItems = string.Join("\n", items.Select(item =>
        {
            var cls = item.Level == 1 ? "report-toc-item report-toc-item-level1" : "report-toc-item report-toc-item-level2";
            return $"<div class=\"{cls}\"><span>{HtmlEscape(item.Title)}</span></div>";
        }));

        return $$"""
            <div class="report-toc">
                <h2 class="report-toc-title">{{HtmlEscape(tocTitle)}}</h2>
                {{tocItems}}
            </div>
            """;
    }

    // ─── Charts (QuickChart.io integration) ────────────────────────

    private static string BuildChartsSection(ReportLayoutDefinition layout)
    {
        if (layout.Charts is not { Count: > 0 })
            return string.Empty;

        var charts = new StringBuilder();
        foreach (var chart in layout.Charts)
        {
            var chartTitle = string.IsNullOrWhiteSpace(chart.Title) ? (chart.Type ?? "Chart") : chart.Title;
            var chartUrl = BuildQuickChartUrl(chart);
            if (string.IsNullOrWhiteSpace(chartUrl))
                continue;

            charts.AppendLine("<div class=\"report-chart\">");
            charts.AppendLine($"<div class=\"report-chart-title\">{HtmlEscape(chartTitle)}</div>");
            charts.AppendLine($"<img src=\"{HtmlAttributeEscape(chartUrl)}\" alt=\"{HtmlAttributeEscape(chartTitle)}\" style=\"max-width:100%;height:auto;\" />");
            charts.AppendLine("</div>");
        }

        if (charts.Length == 0)
            return string.Empty;

        return $$"""
            <div class="report-charts">
                {{charts}}
            </div>
            """;
    }

    private static string? BuildQuickChartUrl(ReportLayoutChartDefinition chart)
    {
        if (string.IsNullOrWhiteSpace(chart.Type))
            return null;

        var w = Math.Clamp(chart.Width, 200, 1200);
        var h = Math.Clamp(chart.Height, 150, 800);

        // Base config for specific chart types
        var chartConfig = chart.Type.ToLowerInvariant() switch
        {
            "gauge" => BuildGaugeConfig(chart),
            _ => BuildStandardChartConfig(chart)
        };

        if (chartConfig is null)
            return null;

        var encoded = Uri.EscapeDataString(chartConfig);
        return $"https://quickchart.io/chart?c={encoded}&w={w}&h={h}";
    }

    private static string BuildStandardChartConfig(ReportLayoutChartDefinition chart)
    {
        var chartType = chart.Type?.ToLowerInvariant() switch
        {
            "horizontalbar" => "horizontalBar",
            "pie" => "pie",
            "doughnut" => "doughnut",
            "line" => "line",
            _ => "bar"
        };

        // Placeholder — data will be injected dynamically in future iterations
        // For now, we generate a chart with labels only as placeholder
        var labels = "[\"A\", \"B\", \"C\"]";
        var data = "[10, 25, 15]";

        return $$"""
            {
                "type": "{{chartType}}",
                "data": {
                    "labels": {{labels}},
                    "datasets": [{
                        "label": "{{chart.Title ?? "Dados"}}",
                        "data": {{data}}
                    }]
                },
                "options": {
                    "plugins": {
                        "title": { "display": true, "text": "{{chart.Title ?? ""}}" }
                    }
                }
            }
            """;
    }

    private static string? BuildGaugeConfig(ReportLayoutChartDefinition chart)
    {
        if (string.IsNullOrWhiteSpace(chart.ValueExpr) && chart.Thresholds is null)
            return null;

        var value = chart.ValueExpr ?? "0";
        var thresholds = chart.Thresholds is { Count: > 0 }
            ? string.Join(",", chart.Thresholds.Select(t => $"{{ \"value\": {t.Value}, \"color\": \"{t.Color ?? "#888"}\" }}"))
            : "";

        return $$"""
            {
                "type": "radialGauge",
                "data": {
                    "datasets": [{
                        "data": [{{value}}],
                        "backgroundColor": ["{{chart.Thresholds?.LastOrDefault()?.Color ?? "#22c55e"}}"]
                    }]
                },
                "options": {
                    "plugins": { "title": { "display": true, "text": "{{chart.Title ?? ""}}" } },
                    "needle": { "radiusPercentage": 2, "widthPercentage": 3.2, "lengthPercentage": 80, "color": "rgba(0,0,0,0.7)" },
                    "valueLabel": { "display": true, "formatter": "{value}%" }
                }
            }
            """;
    }

    // ─── Page Header / Footer ──────────────────────────────────────

    private static string BuildPageHeader(ReportLayoutDefinition layout)
    {
        var ph = layout.PageHeader;
        if (ph is null) return "";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ph.Left)) parts.Add(ph.Left);
        if (!string.IsNullOrWhiteSpace(ph.Center)) parts.Add(ph.Center);
        if (!string.IsNullOrWhiteSpace(ph.Right)) parts.Add(ph.Right);
        return string.Join(" | ", parts);
    }

    private static string BuildPageFooter(ReportLayoutDefinition layout)
    {
        var pf = layout.PageFooter;
        if (pf is null) return "";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(pf.Left)) parts.Add(pf.Left);
        if (!string.IsNullOrWhiteSpace(pf.Center)) parts.Add(pf.Center);
        if (!string.IsNullOrWhiteSpace(pf.Right)) parts.Add(pf.Right);
        return string.Join(" | ", parts);
    }

    // ─── Watermark ─────────────────────────────────────────────────

    private static string BuildWatermark(ReportLayoutDefinition layout)
    {
        if (layout.Watermark is not { } wm || string.IsNullOrWhiteSpace(wm.Text))
            return string.Empty;

        return $$"""
            <div class="report-watermark">
                {{HtmlEscape(wm.Text)}}
            </div>
            """;
    }

    private sealed record ReportLayoutColumn(string Field, string Header, string? Format, string? SectionTitle, ReportLayoutConditionalFormat? ConditionalFormat = null);
}
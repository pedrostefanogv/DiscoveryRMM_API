using System.Globalization;
using System.Text;
using System.Text.Json;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

public class ReportHtmlComposer : IReportHtmlComposer
{
    private const int DefaultPortraitRowsPerPage = 32;
    private const int DefaultLandscapeRowsPerPage = 24;

    public string Compose(ReportRenderContext context, ReportQueryResult data)
    {
        var layout = ReportLayoutDefinitionParser.ParseOrDefault(context.LayoutJson);
        var columns = ResolveColumns(layout, data);
        var rowsPerPage = ResolveRowsPerPage(layout);
        var generatedAtUtc = DateTime.UtcNow;
        var generatedFooterText = BuildGeneratedFooterText(data.Rows.Count, generatedAtUtc);
        var generatedFooterHtml = BuildGeneratedFooter(generatedFooterText);
        var logoUrl = layout.LogoUrl ?? layout.Style?.LogoUrl;
        var style = layout.Style ?? new ReportLayoutStyleDefinition();
        var alternateRowBackground = ResolveAlternateRowBackground(style);

        // Computed fields — apply before anything else that reads data
        var enrichedRows = BuildComputedRows(layout, data.Rows);
        var enrichedData = new ReportQueryResult { Columns = data.Columns, Rows = enrichedRows };

        var content = string.IsNullOrWhiteSpace(layout.GroupBy)
            ? BuildUngroupedContent(layout, columns, enrichedRows, rowsPerPage, generatedFooterHtml)
            : BuildGroupedSections(layout, columns, enrichedRows, rowsPerPage, generatedFooterHtml);

        var subtitleHtml = string.IsNullOrWhiteSpace(context.Subtitle)
            ? string.Empty
            : $"<p class=\"report-subtitle\">{HtmlEscape(context.Subtitle)}</p>";

        var logoHtml = string.IsNullOrWhiteSpace(logoUrl)
            ? string.Empty
            : $"<img class=\"report-logo\" src=\"{HtmlAttributeEscape(logoUrl)}\" alt=\"logo\" />";

        // Cover page (before the shell)
        var coverPageHtml = BuildCoverPage(layout, context, enrichedRows.Count, logoUrl, style);

        // Table of Contents
        var tocHtml = BuildTableOfContents(layout);

        // Charts
        var chartsHtml = BuildChartsSection(layout, enrichedData);

        // Page header/footer
        var pageHeaderCssContent = BuildPageHeaderCssContent(layout);
        var pageFooterCssContent = BuildPageFooterCssContent(layout, generatedFooterText);

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
                        --report-alt-row: {{alternateRowBackground}};
                        --report-border: {{CssValueOrDefault(style.BorderColor, "#d9e2ec")}};
                        --report-muted: {{CssValueOrDefault(style.SecondaryColor, "#52606d")}};
                        --report-font: {{CssValueOrDefault(style.FontFamily, "Arial, sans-serif")}};
                    }

                    @page {
                        size: A4 {{layout.Orientation ?? "portrait"}};
                        margin: 20mm 15mm 25mm 15mm;
                        @top-center { content: {{pageHeaderCssContent}}; font-size: 10px; color: var(--report-muted); }
                        @bottom-center { content: {{pageFooterCssContent}}; font-size: 9px; color: var(--report-muted); }
                    }

                    body { font-family: var(--report-font); margin: 0; color: #1f2933; background: #ffffff; position: relative; }
                    .report-shell { padding: 12px 6px; max-width: 190mm; margin: 0 auto; }
                    .report-header { display:flex; justify-content:space-between; align-items:flex-start; gap:20px; border-bottom:3px solid var(--report-primary); padding-bottom:14px; margin-bottom:18px; }
                    .report-title { margin:0; color:var(--report-primary); font-size:26px; }
                    .report-subtitle { margin:6px 0 0; color:var(--report-muted); font-size:13px; }
                    .report-logo { max-height: {{Math.Clamp(style.LogoMaxHeightPx ?? 56, 24, 180)}}px; max-width:220px; object-fit:contain; }
                    .report-group { margin: 18px 0 24px; page-break-inside: auto; break-inside: auto; }
                    .report-group-title { margin: 0 0 8px; font-size: 18px; color: var(--report-primary); }
                    .report-group-meta { margin: 0 0 10px; color: var(--report-muted); font-size: 12px; }
                    .details-grid { display:grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 10px; margin: 12px 0 14px; }
                    .detail-card { padding: 10px 12px; border:1px solid var(--report-border); border-radius:10px; background:#fff; }
                    .detail-label { font-size:11px; color:var(--report-muted); text-transform:uppercase; letter-spacing:0.04em; }
                    .detail-value { margin-top:6px; font-size:14px; font-weight:600; }
                    table { width:100%; border-collapse:collapse; table-layout:fixed; margin-top:10px; }
                    thead { display: table-header-group; }
                    tfoot { display: table-footer-group; }
                    tr { break-inside: avoid; page-break-inside: avoid; }
                    th, td { border:1px solid var(--report-border); padding:8px 10px; font-size:12px; text-align:left; vertical-align:top; word-wrap:break-word; }
                    th { background:var(--report-header-bg); color:var(--report-header-text); font-weight:700; }
                    tbody tr:nth-child(even) { background:var(--report-alt-row); }
                    .section-caption { margin: 16px 0 6px; color: var(--report-muted); font-size:11px; font-weight:700; text-transform:uppercase; letter-spacing:0.04em; }
                    .report-page-break { height: 0; margin: 0; border: 0; }
                    .report-generated-footer { margin-top: 18px; padding-top: 10px; border-top: 1px solid var(--report-border); color: var(--report-muted); font-size: 11px; font-style: italic; display: flex; justify-content: space-between; gap: 12px; flex-wrap: wrap; }

                    @media print {
                        .report-page-break { page-break-after: always; break-after: page; }
                    }

                    @media screen {
                        .report-page-break { border-top: 1px dashed var(--report-border); margin: 18px 0; }
                    }

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
                    .report-watermark { position:fixed; top:0; left:0; width:100%; height:100%; pointer-events:none; z-index:-1; display:flex; align-items:center; justify-content:center; overflow:hidden; }
                    .report-watermark-text { font-size:{{layout.Watermark?.FontSize ?? 120}}px; color:{{CssValueOrDefault(layout.Watermark?.Color, "#000000")}}; transform:rotate({{layout.Watermark?.Angle ?? -45}}deg); white-space:nowrap; }
                    .report-watermark-image { width:70%; height:70%; object-fit:contain; }
                    .report-watermark-image-cover { width:100%; height:100%; object-fit:cover; }
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

    private static string BuildGroupedSections(ReportLayoutDefinition layout, IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int rowsPerPage, string generatedFooterHtml)
    {
        if (layout.GroupLevels is { Count: > 0 })
            return BuildNestedGroupedSections(layout, columns, rows, rowsPerPage, generatedFooterHtml, layout.GroupLevels, 0);

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
            AppendMainAndSectionTables(builder, layout, FilterColumnsForGrouping(columns, layout), groupRows, deduplicateMainRows: true, rowsPerPage, generatedFooterHtml);
            builder.Append("</section>");
        }

        return builder.ToString();
    }

    private static string BuildNestedGroupedSections(ReportLayoutDefinition layout, IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int rowsPerPage, string generatedFooterHtml, IReadOnlyList<ReportLayoutGroupLevelDefinition> levels, int levelIndex)
    {
        if (levelIndex >= levels.Count)
        {
            return BuildUngroupedContent(layout, columns, rows, rowsPerPage, generatedFooterHtml);
        }

        var level = levels[levelIndex];
        var field = level.Field;
        if (string.IsNullOrWhiteSpace(field))
            return BuildUngroupedContent(layout, columns, rows, rowsPerPage, generatedFooterHtml);

        var grouped = rows.GroupBy(row => GetGroupValue(row, field))
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        foreach (var group in grouped)
        {
            var groupRows = group.ToList();
            var titleTemplate = level.TitleTemplate ?? "{{value}} ({{count}})";
            var title = titleTemplate
                .Replace("{{value}}", group.Key ?? "Nao informado", StringComparison.OrdinalIgnoreCase)
                .Replace("{{count}}", groupRows.Count.ToString(), StringComparison.OrdinalIgnoreCase);

            builder.Append("<section class=\"report-group\">");
            builder.Append($"<h2 class=\"report-group-title\">{HtmlEscape(title)}</h2>");
            builder.Append($"<p class=\"report-group-meta\">{groupRows.Count} registro(s)</p>");

            // Show group details for the first level only (leaf levels use standard columns)
            if (levelIndex == 0 && layout.GroupDetails is { Count: > 0 })
                builder.Append(BuildDetailsGrid(layout.GroupDetails, groupRows.FirstOrDefault()));

            if (layout.GroupSummaries is { Count: > 0 })
                builder.Append(BuildSummaryCards(layout.GroupSummaries, groupRows));

            // Recurse or render leaf
            if (levelIndex + 1 < levels.Count)
            {
                builder.Append(BuildNestedGroupedSections(layout, columns, groupRows, rowsPerPage, generatedFooterHtml, levels, levelIndex + 1));
            }
            else
            {
                AppendMainAndSectionTables(builder, layout, FilterColumnsForGrouping(columns, layout), groupRows, deduplicateMainRows: true, rowsPerPage, generatedFooterHtml);
            }

            builder.Append("</section>");
        }

        return builder.ToString();
    }

    private static string BuildUngroupedContent(ReportLayoutDefinition layout, IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int rowsPerPage, string generatedFooterHtml)
    {
        var builder = new StringBuilder();
        if (layout.Summaries is { Count: > 0 })
            builder.Append(BuildSummaryCards(layout.Summaries, rows));
        AppendMainAndSectionTables(builder, layout, columns, rows, deduplicateMainRows: false, rowsPerPage, generatedFooterHtml);
        return builder.ToString();
    }

    private static void AppendMainAndSectionTables(StringBuilder builder, ReportLayoutDefinition layout, IReadOnlyList<ReportLayoutColumn> mainColumns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, bool deduplicateMainRows, int rowsPerPage, string generatedFooterHtml)
    {
        if (mainColumns.Count > 0)
        {
            var mainRows = deduplicateMainRows
                ? DistinctRowsForColumns(rows, mainColumns)
                : rows;
            builder.Append(BuildSingleTable(mainColumns, mainRows, rowsPerPage, generatedFooterHtml));
        }

        if (layout.Sections is { Count: > 0 })
            builder.Append(BuildSectionTables(layout, rows, rowsPerPage, generatedFooterHtml));
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> DistinctRowsForColumns(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<ReportLayoutColumn> columns)
    {
        if (rows.Count <= 1 || columns.Count == 0)
            return rows;

        var deduplicated = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var key = BuildRowDistinctKey(row, columns);
            if (seen.Add(key))
                deduplicated.Add(row);
        }

        return deduplicated;
    }

    private static string BuildRowDistinctKey(IReadOnlyDictionary<string, object?> row, IReadOnlyList<ReportLayoutColumn> columns)
    {
        var builder = new StringBuilder();
        foreach (var column in columns)
        {
            row.TryGetValue(column.Field, out var value);
            var normalized = NormalizeDistinctValue(value);
            builder.Append(normalized.Length).Append(':').Append(normalized).Append('|');
        }

        return builder.ToString();
    }

    private static string NormalizeDistinctValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string BuildSectionTables(ReportLayoutDefinition layout, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int rowsPerPage, string generatedFooterHtml)
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

            builder.Append(BuildSingleTable(columns, rows, rowsPerPage, generatedFooterHtml));
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

    private static string BuildSingleTable(IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int rowsPerPage, string generatedFooterHtml)
    {
        var safeRowsPerPage = Math.Clamp(rowsPerPage, 10, 200);
        var totalPages = rows.Count == 0 ? 1 : (int)Math.Ceiling(rows.Count / (double)safeRowsPerPage);
        var baseCaption = columns.Select(column => column.SectionTitle).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
        var builder = new StringBuilder();

        if (rows.Count == 0)
        {
            builder.Append(BuildTablePage(columns, rows, baseCaption));
            builder.Append(generatedFooterHtml);
            return builder.ToString();
        }

        for (var pageIndex = 0; pageIndex < totalPages; pageIndex++)
        {
            var chunk = rows.Skip(pageIndex * safeRowsPerPage).Take(safeRowsPerPage).ToList();
            var caption = baseCaption;
            if (totalPages > 1)
            {
                var pageLabel = $"Pagina {pageIndex + 1}/{totalPages}";
                caption = string.IsNullOrWhiteSpace(caption) ? pageLabel : $"{caption} - {pageLabel}";
            }

            builder.Append(BuildTablePage(columns, chunk, caption));
            builder.Append(generatedFooterHtml);
            if (pageIndex < totalPages - 1)
                builder.Append("<div class=\"report-page-break\"></div>");
        }

        return builder.ToString();
    }

    private static string BuildTablePage(IReadOnlyList<ReportLayoutColumn> columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string? caption)
    {
        var headers = string.Join(string.Empty, columns.Select(column => $"<th>{HtmlEscape(column.Header)}</th>"));
        var captionHtml = string.IsNullOrWhiteSpace(caption) ? string.Empty : $"<div class=\"section-caption\">{HtmlEscape(caption)}</div>";
        var rowsHtml = string.Join("\n", rows.Select(row =>
        {
            var cells = columns.Select(column =>
            {
                row.TryGetValue(column.Field, out var value);
                var formattedValue = FormatValue(value, column.Format);
                var style = ResolveConditionalCellStyle(column.ConditionalFormat, value);
                var icon = ResolveConditionalIcon(column.ConditionalFormat, value);
                var displayValue = string.IsNullOrWhiteSpace(icon) ? formattedValue : $"{icon} {formattedValue}";
                var styleAttr = string.IsNullOrWhiteSpace(style) ? "" : $" style=\"{style}\"";
                return $"<td{styleAttr}>{HtmlEscape(displayValue)}</td>";
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

        if (string.Equals(summary.Aggregate, "countIf", StringComparison.OrdinalIgnoreCase))
        {
            if (summary.Condition is null)
                return rows.Count(r => r.TryGetValue(summary.Field, out var v) && v is bool b && b);
            return rows.Count(row => EvaluateConditionAgainstSummary(row, summary.Field!, summary.Condition.Value));
        }

        if (string.Equals(summary.Aggregate, "sumIf", StringComparison.OrdinalIgnoreCase))
        {
            decimal sumIf = 0;
            foreach (var row in rows)
            {
                if (summary.Condition is { } conditionValue && EvaluateConditionAgainstSummary(row, summary.Field!, conditionValue))
                {
                    if (row.TryGetValue(summary.Field, out var v) && v is not null && TryConvertToDecimal(v, out var dv))
                        sumIf += dv;
                }
            }
            return sumIf;
        }

        if (string.Equals(summary.Aggregate, "compliancePercent", StringComparison.OrdinalIgnoreCase))
        {
            var total = rows.Count;
            if (total == 0) return 0m;
            var compliant = rows.Count(r =>
            {
                if (!r.TryGetValue(summary.Field, out var v)) return true;
                return v is not bool b || !b;
            });
            return Math.Round((decimal)compliant / total * 100, 1);
        }

        return null;
    }

    private static bool EvaluateConditionAgainstSummary(IReadOnlyDictionary<string, object?> row, string field, JsonElement condition)
    {
        if (condition.ValueKind != JsonValueKind.Object)
            return false;

        // Try "eq" condition
        if (condition.TryGetProperty("eq", out var eqValue))
        {
            row.TryGetValue(field, out var rowValue);
            var expected = eqValue.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.String => eqValue.GetString() ?? "",
                _ => eqValue.ToString()
            };
            var actual = rowValue switch
            {
                bool b => b.ToString().ToLowerInvariant(),
                _ => rowValue?.ToString() ?? ""
            };
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        return false;
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

    private static string ResolveAlternateRowBackground(ReportLayoutStyleDefinition style)
    {
        if (style.ShowRowStripes is false)
            return "transparent";

        var rawColor = CssValueOrDefault(style.AlternateRowColor, "#f5f7fb");
        return TryParseHexColor(rawColor, out var red, out var green, out var blue)
            ? $"rgba({red}, {green}, {blue}, 0.35)"
            : rawColor;
    }

    private static bool TryParseHexColor(string? value, out int red, out int green, out int blue)
    {
        red = 0;
        green = 0;
        blue = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var hex = value.Trim();
        if (!hex.StartsWith('#'))
            return false;

        if (hex.Length == 4)
        {
            hex = $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}";
        }

        if (hex.Length != 7)
            return false;

        return int.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red)
            && int.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green)
            && int.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue);
    }

    private static string HtmlAttributeEscape(string? text) => System.Net.WebUtility.HtmlEncode(text ?? string.Empty);
    private static string HtmlEscape(string? text) => System.Net.WebUtility.HtmlEncode(text ?? string.Empty);

    private static string ResolveDisplayHeader(ReportLayoutColumnDefinition column)
    {
        var display = column.DisplayHeader;
        return string.IsNullOrWhiteSpace(display) ? (column.Field ?? string.Empty) : display;
    }

    // Cover Page

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

    // Table of Contents

    private static string BuildTableOfContents(ReportLayoutDefinition layout)
    {
        if (layout.TableOfContents is not { Enabled: true })
            return string.Empty;

        var tocTitle = string.IsNullOrWhiteSpace(layout.TableOfContents.Title) ? "Indice" : layout.TableOfContents.Title;

        // Build TOC from group titles
        var items = new List<(string Title, int Level)>();
        items.Add((layout.Title ?? "Relatorio", 1));

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

    // Computed Fields

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> BuildComputedRows(ReportLayoutDefinition layout, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (layout.ComputedFields is not { Count: > 0 })
            return rows;

        var result = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var enriched = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
            foreach (var computed in layout.ComputedFields)
            {
                if (string.IsNullOrWhiteSpace(computed.Name) || string.IsNullOrWhiteSpace(computed.Expression))
                    continue;
                enriched[computed.Name] = EvaluateComputedExpression(computed.Expression, row);
            }
            result.Add(enriched);
        }
        return result;
    }

    private static object? EvaluateComputedExpression(string expression, IReadOnlyDictionary<string, object?> row)
    {
        try
        {
            var resolved = new StringBuilder(expression);
            foreach (var key in row.Keys.OrderByDescending(k => k.Length))
            {
                if (!row.TryGetValue(key, out var val) || val is null)
                    continue;
                resolved.Replace(key, FormatNumericLiteral(val));
            }

            var resolvedExpr = resolved.ToString();

            var divMatch = System.Text.RegularExpressions.Regex.Match(resolvedExpr, @"^\s*([0-9.]+)\s*/\s*([0-9.]+)\s*$");
            if (divMatch.Success && decimal.TryParse(divMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d1)
                && decimal.TryParse(divMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) && d2 != 0)
                return Math.Round(d1 / d2, 2);

            var subMatch = System.Text.RegularExpressions.Regex.Match(resolvedExpr, @"^\s*([0-9.]+)\s*-\s*([0-9.]+)\s*$");
            if (subMatch.Success && decimal.TryParse(subMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var s1)
                && decimal.TryParse(subMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var s2))
                return s1 - s2;

            var mulMatch = System.Text.RegularExpressions.Regex.Match(resolvedExpr, @"^\s*([0-9.]+)\s*\*\s*([0-9.]+)\s*$");
            if (mulMatch.Success && decimal.TryParse(mulMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var m1)
                && decimal.TryParse(mulMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var m2))
                return Math.Round(m1 * m2, 2);

            var addMatch = System.Text.RegularExpressions.Regex.Match(resolvedExpr, @"^\s*([0-9.]+)\s*\+\s*([0-9.]+)\s*$");
            if (addMatch.Success && decimal.TryParse(addMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var a1)
                && decimal.TryParse(addMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var a2))
                return a1 + a2;

            var ternaryMatch = System.Text.RegularExpressions.Regex.Match(resolvedExpr, @"^(.+?)\s*\?\s*(.+?)\s*:\s*(.+)$");
            if (ternaryMatch.Success)
            {
                var cond = ternaryMatch.Groups[1].Value.Trim();
                var trueVal = ternaryMatch.Groups[2].Value.Trim();
                var falseVal = ternaryMatch.Groups[3].Value.Trim();
                return EvaluateTernaryCondition(cond, row) ? trueVal.Trim('\'', '"') : falseVal.Trim('\'', '"');
            }

            return resolvedExpr;
        }
        catch
        {
            return null;
        }
    }

    private static bool EvaluateTernaryCondition(string condition, IReadOnlyDictionary<string, object?> row)
    {
        var neMatch = System.Text.RegularExpressions.Regex.Match(condition, @"(\w+)\s*!=\s*null", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (neMatch.Success)
        {
            var fieldName = neMatch.Groups[1].Value;
            return row.TryGetValue(fieldName, out var val) && val is not null;
        }

        var eqMatch = System.Text.RegularExpressions.Regex.Match(condition, @"(\w+)\s*==\s*(.+)");
        if (eqMatch.Success)
        {
            var fieldName = eqMatch.Groups[1].Value;
            var expected = eqMatch.Groups[2].Value.Trim().Trim('\'', '"');
            row.TryGetValue(fieldName, out var val);
            var strVal = val switch
            {
                bool b => b.ToString().ToLowerInvariant(),
                _ => val?.ToString() ?? ""
            };
            return string.Equals(strVal, expected, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string FormatNumericLiteral(object value)
    {
        return value switch
        {
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "0"
        };
    }

    // Charts (QuickChart.io integration)

    private static string BuildChartsSection(ReportLayoutDefinition layout, ReportQueryResult data)
    {
        if (layout.Charts is not { Count: > 0 })
            return string.Empty;

        var charts = new StringBuilder();
        foreach (var chart in layout.Charts)
        {
            var chartTitle = string.IsNullOrWhiteSpace(chart.Title) ? (chart.Type ?? "Chart") : chart.Title;
            var chartUrl = BuildQuickChartUrl(chart, data.Rows);
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

    private static string? BuildQuickChartUrl(ReportLayoutChartDefinition chart, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.IsNullOrWhiteSpace(chart.Type))
            return null;

        var w = Math.Clamp(chart.Width, 200, 1200);
        var h = Math.Clamp(chart.Height, 150, 800);

        var chartConfig = chart.Type.ToLowerInvariant() switch
        {
            "gauge" => BuildGaugeConfig(chart, rows),
            _ => BuildStandardChartConfig(chart, rows)
        };

        if (chartConfig is null)
            return null;

        var encoded = Uri.EscapeDataString(chartConfig);
        return $"https://quickchart.io/chart?c={encoded}&w={w}&h={h}";
    }

    private static string? BuildStandardChartConfig(ReportLayoutChartDefinition chart, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var chartType = chart.Type?.ToLowerInvariant() switch
        {
            "horizontalbar" => "horizontalBar",
            "pie" => "pie",
            "doughnut" => "doughnut",
            "line" => "line",
            "stackedbar" => "bar",
            _ => "bar"
        };

        var aggregate = string.IsNullOrWhiteSpace(chart.Aggregate) ? "count" : chart.Aggregate.ToLowerInvariant();
        var limit = chart.Limit > 0 ? chart.Limit : 15;

        var series = BuildChartDataSeries(rows, chart.GroupField, chart.ValueField, aggregate, limit, chart.BucketBy);
        if (series.Labels.Count == 0)
            return null;

        var labelsJson = System.Text.Json.JsonSerializer.Serialize(series.Labels);
        var dataJson = System.Text.Json.JsonSerializer.Serialize(series.Values);
        var title = chart.Title ?? "Dados";

        var stackedOption = chartType == "bar" && string.Equals(chart.Type, "stackedbar", StringComparison.OrdinalIgnoreCase)
            ? ", \"stacked\": true"
            : "";

        return $$"""
            {
                "type": "{{chartType}}",
                "data": {
                    "labels": {{labelsJson}},
                    "datasets": [{
                        "label": "{{title}}",
                        "data": {{dataJson}}{{stackedOption}}
                    }]
                },
                "options": {
                    "plugins": {
                        "title": { "display": true, "text": "{{title}}" },
                        "legend": { "display": false }
                    }
                }
            }
            """;
    }

    private static string? BuildGaugeConfig(ReportLayoutChartDefinition chart, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var rawValue = ComputeGaugeValue(chart, rows);
        if (rawValue is null && chart.Thresholds is null)
            return null;

        var gaugeValue = rawValue ?? 0;
        var needleColor = GetGaugeNeedleColor(chart.Thresholds, Convert.ToDouble(gaugeValue));

        return $$"""
            {
                "type": "radialGauge",
                "data": {
                    "datasets": [{
                        "data": [{{gaugeValue}}],
                        "backgroundColor": ["{{needleColor}}"]
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

    private static object? ComputeGaugeValue(ReportLayoutChartDefinition chart, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (string.IsNullOrWhiteSpace(chart.GroupField) && string.IsNullOrWhiteSpace(chart.Aggregate))
            return null;

        if (!string.IsNullOrWhiteSpace(chart.GroupField) && !string.IsNullOrWhiteSpace(chart.Aggregate))
        {
            var aggregate = chart.Aggregate.ToLowerInvariant();
            var valueField = chart.ValueField;

            if (aggregate == "count" && !string.IsNullOrWhiteSpace(valueField))
            {
                var total = rows.Count;
                if (total == 0) return 0;
                var matching = rows.Count(r => r.TryGetValue(valueField, out var v) && v is bool b && b);
                return Math.Round((decimal)matching / total * 100, 1);
            }

            if (aggregate == "compliancePercent")
            {
                var total = rows.Count;
                if (total == 0) return 0;
                var compliant = rows.Count(r =>
                {
                    if (!r.TryGetValue(chart.GroupField, out var v)) return true;
                    return v is not bool b || !b;
                });
                return Math.Round((decimal)compliant / total * 100, 1);
            }

            if (aggregate is "avg" or "sum" && !string.IsNullOrWhiteSpace(valueField))
            {
                decimal sum = 0;
                int count = 0;
                foreach (var row in rows)
                {
                    if (row.TryGetValue(valueField, out var v) && TryConvertToDecimal(v, out var dv))
                    {
                        sum += dv;
                        count++;
                    }
                }
                return count > 0 ? Math.Round(aggregate == "avg" ? sum / count : sum, 1) : null;
            }
        }

        return null;
    }

    private static string GetGaugeNeedleColor(IReadOnlyList<ReportLayoutChartThreshold>? thresholds, double value)
    {
        if (thresholds is not { Count: > 0 })
            return "#22c55e";

        string? color = null;
        foreach (var t in thresholds.OrderBy(t => t.Value))
        {
            if (value <= t.Value)
            {
                color = t.Color;
                break;
            }
        }
        return string.IsNullOrWhiteSpace(color) ? thresholds.Last().Color ?? "#22c55e" : color!;
    }

    private sealed record ChartDataSeries(List<string> Labels, List<double> Values);

    private static ChartDataSeries BuildChartDataSeries(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string? groupField, string? valueField, string aggregate, int limit, string? bucketBy)
    {
        if (string.IsNullOrWhiteSpace(groupField))
            return new ChartDataSeries([], []);

        var grouped = rows
            .Where(r => r.TryGetValue(groupField, out var v) && v is not null)
            .GroupBy(r =>
            {
                r.TryGetValue(groupField, out var v);
                var raw = v?.ToString() ?? "N/A";

                if (!string.IsNullOrWhiteSpace(bucketBy) && v is DateTime dt)
                {
                    return bucketBy.ToLowerInvariant() switch
                    {
                        "hour" => dt.ToString("yyyy-MM-dd HH:00"),
                        "day" => dt.ToString("yyyy-MM-dd"),
                        "week" => $"Week {System.Globalization.ISOWeek.GetWeekOfYear(dt)}",
                        "month" => dt.ToString("yyyy-MM"),
                        _ => raw
                    };
                }
                return raw;
            })
            .Select(group => (
                Label: group.Key,
                Value: ComputeGroupAggregate(group.ToList(), valueField, aggregate)
            ))
            .Where(item => item.Value.HasValue)
            .OrderByDescending(item => item.Value!.Value)
            .Take(limit)
            .ToList();

        if (!string.IsNullOrWhiteSpace(bucketBy))
            grouped = grouped.OrderBy(item => item.Label, StringComparer.Ordinal).ToList();

        return new ChartDataSeries(
            grouped.Select(item => item.Label).ToList(),
            grouped.Select(item => (double)item.Value!.Value).ToList()
        );
    }

    private static double? ComputeGroupAggregate(IReadOnlyList<IReadOnlyDictionary<string, object?>> groupRows, string? valueField, string aggregate)
    {
        switch (aggregate.ToLowerInvariant())
        {
            case "count":
                return groupRows.Count;
            case "countdistinct":
                if (string.IsNullOrWhiteSpace(valueField))
                    return groupRows.Count;
                return groupRows
                    .Where(r => r.TryGetValue(valueField, out var v) && v is not null)
                    .Select(r => r[valueField]?.ToString())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
            case "sum":
            case "avg":
                if (string.IsNullOrWhiteSpace(valueField))
                    return null;
                decimal total = 0;
                int count = 0;
                foreach (var row in groupRows)
                {
                    if (row.TryGetValue(valueField, out var v) && TryConvertToDecimal(v, out var dv))
                    {
                        total += dv;
                        count++;
                    }
                }
                if (count == 0) return null;
                return aggregate == "avg" ? (double)(total / count) : (double)total;
            case "min":
                if (string.IsNullOrWhiteSpace(valueField)) return null;
                decimal? min = null;
                foreach (var row in groupRows)
                {
                    if (row.TryGetValue(valueField, out var v) && TryConvertToDecimal(v, out var dv))
                        min = min is null ? dv : dv < min ? dv : min;
                }
                return min.HasValue ? (double)min.Value : null;
            case "max":
                if (string.IsNullOrWhiteSpace(valueField)) return null;
                decimal? max = null;
                foreach (var row in groupRows)
                {
                    if (row.TryGetValue(valueField, out var v) && TryConvertToDecimal(v, out var dv))
                        max = max is null ? dv : dv > max ? dv : max;
                }
                return max.HasValue ? (double)max.Value : null;
            default:
                return groupRows.Count;
        }
    }

    // Page Header / Footer

    private static int ResolveRowsPerPage(ReportLayoutDefinition layout)
    {
        return string.Equals(layout.Orientation, "landscape", StringComparison.OrdinalIgnoreCase)
            ? DefaultLandscapeRowsPerPage
            : DefaultPortraitRowsPerPage;
    }

    private static string BuildPageHeaderCssContent(ReportLayoutDefinition layout)
    {
        return CssStringLiteral(BuildPageHeader(layout));
    }

    private static string BuildPageFooterCssContent(ReportLayoutDefinition layout, string generatedFooterText)
    {
        var footerParts = new List<string>();
        var footer = BuildPageFooter(layout);
        if (!string.IsNullOrWhiteSpace(footer))
            footerParts.Add(footer);

        if (!string.IsNullOrWhiteSpace(generatedFooterText))
            footerParts.Add(generatedFooterText);

        var prefix = footerParts.Count == 0
            ? "Pagina "
            : $"{string.Join(" | ", footerParts)} | Pagina ";

        return $"{CssStringLiteral(prefix)} counter(page) \" de \" counter(pages)";
    }

    private static string CssStringLiteral(string? value)
    {
        var escaped = (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string BuildGeneratedFooterText(int rowCount, DateTime generatedAtUtc)
    {
        var timestamp = generatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return $"Gerado por Discovery RMM em {timestamp} UTC | {rowCount} registro(s)";
    }

    private static string BuildGeneratedFooter(string generatedFooterText)
    {
        return $$"""
            <div class="report-generated-footer">
                <span>{{HtmlEscape(generatedFooterText)}}</span>
            </div>
            """;
    }

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

    // Watermark

    private static string BuildWatermark(ReportLayoutDefinition layout)
    {
        if (layout.Watermark is not { } wm)
            return string.Empty;

        var watermarkImageUrl = ResolveWatermarkImageUrl(layout, wm);
        if (!string.IsNullOrWhiteSpace(watermarkImageUrl))
        {
            var cssClass = string.Equals(wm.ImageFit, "cover", StringComparison.OrdinalIgnoreCase)
                ? "report-watermark-image report-watermark-image-cover"
                : "report-watermark-image";

            var opacity = Math.Clamp(wm.ImageOpacity ?? 0.08, 0.01, 0.4)
                .ToString("0.##", CultureInfo.InvariantCulture);

            return $$"""
                <div class="report-watermark" style="opacity:{{opacity}};">
                    <img class="{{cssClass}}" src="{{HtmlAttributeEscape(watermarkImageUrl)}}" alt="watermark" />
                </div>
                """;
        }

        if (string.IsNullOrWhiteSpace(wm.Text))
            return string.Empty;

        return $$"""
            <div class="report-watermark" style="opacity:0.06;">
                <div class="report-watermark-text">{{HtmlEscape(wm.Text)}}</div>
            </div>
            """;
    }

    private static string? ResolveWatermarkImageUrl(ReportLayoutDefinition layout, ReportLayoutWatermarkDefinition watermark)
    {
        if (!string.IsNullOrWhiteSpace(watermark.LogoUrl))
            return watermark.LogoUrl;

        if (!string.IsNullOrWhiteSpace(watermark.ImageUrl))
            return watermark.ImageUrl;

        if (watermark.UseLogo)
            return layout.LogoUrl ?? layout.Style?.LogoUrl;

        return null;
    }

    private sealed record ReportLayoutColumn(string Field, string Header, string? Format, string? SectionTitle, ReportLayoutConditionalFormat? ConditionalFormat = null);
}

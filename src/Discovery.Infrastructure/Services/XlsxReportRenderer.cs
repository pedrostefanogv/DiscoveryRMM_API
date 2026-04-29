using ClosedXML.Excel;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

public class XlsxReportRenderer : IReportRenderer
{
    public ReportFormat Format => ReportFormat.Xlsx;

    public Task<ReportDocument> RenderAsync(ReportRenderContext context, ReportQueryResult data, CancellationToken cancellationToken = default)
    {
        var columns = ResolveColumns(context.LayoutJson, data.Columns);
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Report");

        worksheet.Cell(1, 1).Value = context.Title;
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;

        if (!string.IsNullOrWhiteSpace(context.Subtitle))
        {
            worksheet.Cell(2, 1).Value = context.Subtitle;
            worksheet.Cell(2, 1).Style.Font.Italic = true;
            worksheet.Cell(2, 1).Style.Font.FontSize = 11;
        }

        var headerRow = string.IsNullOrWhiteSpace(context.Subtitle) ? 3 : 4;
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            var cell = worksheet.Cell(headerRow, columnIndex + 1);
            cell.Value = columns[columnIndex].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        var rowNumber = headerRow + 1;
        foreach (var row in data.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var col = columns[columnIndex];
                row.TryGetValue(col.Field, out var value);
                var formatted = FormatCellValue(value, col.Format);
                var cell = worksheet.Cell(rowNumber, columnIndex + 1);

                if (value is DateTime dt)
                    cell.Value = dt;
                else if (value is DateTimeOffset dto)
                    cell.Value = dto.DateTime;
                else if (value is decimal or double or float or int or long)
                    cell.Value = Convert.ToDouble(value);
                else
                    cell.Value = value?.ToString() ?? string.Empty;

                // Apply conditional formatting
                if (col.ConditionalFormat?.Rules is { Count: > 0 })
                {
                    foreach (var rule in col.ConditionalFormat.Rules)
                    {
                        if (!EvaluateCondition(rule.Operator, value, rule.Value))
                            continue;

                        if (!string.IsNullOrWhiteSpace(rule.BackgroundColor))
                        {
                            try { cell.Style.Fill.BackgroundColor = XLColor.FromHtml(rule.BackgroundColor); }
                            catch { /* ignore invalid color */ }
                        }
                        if (!string.IsNullOrWhiteSpace(rule.TextColor))
                        {
                            try { cell.Style.Font.FontColor = XLColor.FromHtml(rule.TextColor); }
                            catch { /* ignore invalid color */ }
                        }
                        break; // first match wins
                    }
                }
            }

            rowNumber++;
        }

        // Auto-fit but cap column width
        worksheet.Columns().AdjustToContents();
        for (var colIndex = 1; colIndex <= columns.Count; colIndex++)
        {
            if (worksheet.Column(colIndex).Width > 60)
                worksheet.Column(colIndex).Width = 60;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return Task.FromResult(new ReportDocument
        {
            Content = stream.ToArray(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileExtension = "xlsx"
        });
    }

    private static string FormatCellValue(object? value, string? format)
    {
        if (value is null) return string.Empty;
        if (string.Equals(format, "datetime", StringComparison.OrdinalIgnoreCase) && value is DateTime dt)
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        if (string.Equals(format, "bytes", StringComparison.OrdinalIgnoreCase) && TryConvertToDecimal(value, out var b))
            return FormatBytes(b);
        if (string.Equals(format, "percent", StringComparison.OrdinalIgnoreCase) && TryConvertToDecimal(value, out var p))
            return $"{p:F1}%";
        return value.ToString() ?? string.Empty;
    }

    private static string FormatBytes(decimal bytes)
    {
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
            case double d: decimalValue = (decimal)d; return true;
            case float f: decimalValue = (decimal)f; return true;
            case int i: decimalValue = i; return true;
            case long l: decimalValue = l; return true;
            default: decimalValue = 0; return false;
        }
    }

    private static bool EvaluateCondition(string? op, object? left, object? right)
    {
        if (op is null || left is null || right is null) return false;
        if (string.Equals(op, "eq", StringComparison.OrdinalIgnoreCase))
            return string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
        if (TryConvertToDecimal(left, out var ln) && TryConvertToDecimal(right, out var rn))
        {
            return op.ToLowerInvariant() switch
            {
                "lt" => ln < rn, "lte" => ln <= rn,
                "gt" => ln > rn, "gte" => ln >= rn,
                _ => false
            };
        }
        return false;
    }

    private static IReadOnlyList<ReportColumnProjection> ResolveColumns(string? layoutJson, IReadOnlyList<string> fallbackColumns)
    {
        var layout = ReportLayoutDefinitionParser.ParseOrDefault(layoutJson);

        if (layout.Columns is { Count: > 0 })
        {
            var directColumns = layout.Columns
                .Where(column => !string.IsNullOrWhiteSpace(column.Field))
                .Select(column => new ReportColumnProjection(column.Field!, ResolveHeader(column), column.Format, column.ConditionalFormat))
                .ToList();

            if (directColumns.Count > 0)
                return directColumns;
        }

        if (layout.Sections is { Count: > 0 })
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sectionColumns = new List<ReportColumnProjection>();

            foreach (var section in layout.Sections)
            {
                if (section.Columns is not { Count: > 0 })
                    continue;

                foreach (var column in section.Columns)
                {
                    if (string.IsNullOrWhiteSpace(column.Field) || !seen.Add(column.Field))
                        continue;

                    sectionColumns.Add(new ReportColumnProjection(column.Field, ResolveHeader(column), column.Format, column.ConditionalFormat));
                }
            }

            if (sectionColumns.Count > 0)
                return sectionColumns;
        }

        return fallbackColumns.Select(column => new ReportColumnProjection(column, column, null, null)).ToList();
    }

    private static string ResolveHeader(ReportLayoutColumnDefinition column)
    {
        var header = string.IsNullOrWhiteSpace(column.DisplayHeader) ? column.Field : column.DisplayHeader;
        return string.IsNullOrWhiteSpace(header) ? string.Empty : header;
    }

    private sealed record ReportColumnProjection(string Field, string Header, string? Format = null, ReportLayoutConditionalFormat? ConditionalFormat = null);
}

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

        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            var headerRow = string.IsNullOrWhiteSpace(context.Subtitle) ? 3 : 4;
            var cell = worksheet.Cell(headerRow, columnIndex + 1);
            cell.Value = columns[columnIndex].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        var rowNumber = string.IsNullOrWhiteSpace(context.Subtitle) ? 4 : 5;
        foreach (var row in data.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var key = columns[columnIndex].Field;
                row.TryGetValue(key, out var value);
                worksheet.Cell(rowNumber, columnIndex + 1).Value = value?.ToString() ?? string.Empty;
            }

            rowNumber++;
        }

        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return Task.FromResult(new ReportDocument
        {
            Content = stream.ToArray(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileExtension = "xlsx"
        });
    }

    private static IReadOnlyList<ReportColumnProjection> ResolveColumns(string? layoutJson, IReadOnlyList<string> fallbackColumns)
    {
        var layout = ReportLayoutDefinitionParser.ParseOrDefault(layoutJson);

        if (layout.Columns is { Count: > 0 })
        {
            var directColumns = layout.Columns
                .Where(column => !string.IsNullOrWhiteSpace(column.Field))
                .Select(column => new ReportColumnProjection(column.Field!, ResolveHeader(column)))
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

                    sectionColumns.Add(new ReportColumnProjection(column.Field, ResolveHeader(column)));
                }
            }

            if (sectionColumns.Count > 0)
                return sectionColumns;
        }

        return fallbackColumns.Select(column => new ReportColumnProjection(column, column)).ToList();
    }

    private static string ResolveHeader(ReportLayoutColumnDefinition column)
    {
        var header = string.IsNullOrWhiteSpace(column.DisplayHeader) ? column.Field : column.DisplayHeader;
        return string.IsNullOrWhiteSpace(header) ? string.Empty : header;
    }

    private sealed record ReportColumnProjection(string Field, string Header);
}

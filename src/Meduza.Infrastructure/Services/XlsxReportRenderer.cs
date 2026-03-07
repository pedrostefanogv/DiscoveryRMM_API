using ClosedXML.Excel;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;

namespace Meduza.Infrastructure.Services;

public class XlsxReportRenderer : IReportRenderer
{
    public ReportFormat Format => ReportFormat.Xlsx;

    public Task<ReportDocument> RenderAsync(string title, ReportQueryResult data, CancellationToken cancellationToken = default)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Report");

        worksheet.Cell(1, 1).Value = title;
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;

        for (var columnIndex = 0; columnIndex < data.Columns.Count; columnIndex++)
        {
            var cell = worksheet.Cell(3, columnIndex + 1);
            cell.Value = data.Columns[columnIndex];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        var rowNumber = 4;
        foreach (var row in data.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var columnIndex = 0; columnIndex < data.Columns.Count; columnIndex++)
            {
                var key = data.Columns[columnIndex];
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
}

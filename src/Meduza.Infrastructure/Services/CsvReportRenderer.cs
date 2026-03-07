using System.Text;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;

namespace Meduza.Infrastructure.Services;

public class CsvReportRenderer : IReportRenderer
{
    public ReportFormat Format => ReportFormat.Csv;

    public Task<ReportDocument> RenderAsync(string title, ReportQueryResult data, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {Escape(title)}");
        sb.AppendLine(string.Join(',', data.Columns.Select(Escape)));

        foreach (var row in data.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var values = new List<string>(data.Columns.Count);
            foreach (var column in data.Columns)
            {
                row.TryGetValue(column, out var value);
                values.Add(Escape(value?.ToString() ?? string.Empty));
            }

            sb.AppendLine(string.Join(',', values));
        }

        return Task.FromResult(new ReportDocument
        {
            Content = Encoding.UTF8.GetBytes(sb.ToString()),
            ContentType = "text/csv",
            FileExtension = "csv"
        });
    }

    private static string Escape(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}

using System.Text;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;

namespace Meduza.Infrastructure.Services;

public class CsvReportRenderer : IReportRenderer
{
    public ReportFormat Format => ReportFormat.Csv;

    public Task<ReportDocument> RenderAsync(ReportRenderContext context, ReportQueryResult data, CancellationToken cancellationToken = default)
    {
        var columns = ResolveColumns(context.LayoutJson, data.Columns);
        var sb = new StringBuilder();

        sb.AppendLine($"# {Escape(context.Title)}");
        sb.AppendLine(string.Join(',', columns.Select(column => Escape(column.Header))));

        foreach (var row in data.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var values = new List<string>(columns.Count);
            foreach (var column in columns)
            {
                row.TryGetValue(column.Field, out var value);
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

    private static string Escape(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}

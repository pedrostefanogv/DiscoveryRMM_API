using System.Globalization;
using System.Text;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Renderer CSV compativel com RFC 4180.
///
/// Correcoes relevantes:
/// - nao emite linha de titulo ("# ..."): isso nao e CSV valido e deslocava a
///   primeira linha nas planilhas;
/// - usa CRLF e adiciona BOM UTF-8, para o Excel (pt-BR) reconhecer acentos;
/// - normaliza quebras de linha dentro do campo, que antes fati
///   a linha em varias;
/// - aplica o mesmo <c>format</c> das colunas usado nos demais renderers
///   (datetime/bytes/percent/number), que antes era ignorado.
/// </summary>
public class CsvReportRenderer : IReportRenderer
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private const string RowSeparator = "\r\n";

    public ReportFormat Format => ReportFormat.Csv;

    public Task<ReportDocument> RenderAsync(ReportRenderContext context, ReportQueryResult data, CancellationToken cancellationToken = default)
    {
        var columns = ResolveColumns(context.LayoutJson, data.Columns);
        var sb = new StringBuilder();

        sb.Append(string.Join(',', columns.Select(column => Escape(NeutralizeFormula(column.Header))))).Append(RowSeparator);

        foreach (var row in data.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var values = new List<string>(columns.Count);
            foreach (var column in columns)
            {
                row.TryGetValue(column.Field, out var value);
                values.Add(Escape(NeutralizeFormula(FormatValue(value, column.Format))));
            }

            sb.Append(string.Join(',', values)).Append(RowSeparator);
        }

        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var content = new byte[Utf8Bom.Length + body.Length];
        Utf8Bom.CopyTo(content, 0);
        body.CopyTo(content, Utf8Bom.Length);

        return Task.FromResult(new ReportDocument
        {
            Content = content,
            ContentType = "text/csv; charset=utf-8",
            FileExtension = "csv"
        });
    }

    private static string FormatValue(object? value, string? format)
    {
        if (value is null) return string.Empty;

        if (string.Equals(format, "bytes", StringComparison.OrdinalIgnoreCase) && TryConvertToDecimal(value, out var bytes))
            return FormatBytes(bytes);

        if (string.Equals(format, "datetime", StringComparison.OrdinalIgnoreCase))
        {
            if (value is DateTime dt) return dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset dto) return dto.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (string.Equals(format, "number", StringComparison.OrdinalIgnoreCase) && TryConvertToDecimal(value, out var num))
            return num.ToString("0.##", CultureInfo.InvariantCulture);

        if (string.Equals(format, "percent", StringComparison.OrdinalIgnoreCase) && TryConvertToDecimal(value, out var pct))
            return pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";

        return value.ToString() ?? string.Empty;
    }

    private static string FormatBytes(decimal bytes)
    {
        if (bytes >= 1_099_511_627_776m) return (bytes / 1_099_511_627_776m).ToString("0.0", CultureInfo.InvariantCulture) + " TB";
        if (bytes >= 1_073_741_824m) return (bytes / 1_073_741_824m).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= 1_048_576m) return (bytes / 1_048_576m).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= 1_024m) return (bytes / 1_024m).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        return bytes.ToString("0", CultureInfo.InvariantCulture) + " B";
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
            default: decimalValue = 0; return false;
        }
    }

    private static IReadOnlyList<ReportColumnProjection> ResolveColumns(string? layoutJson, IReadOnlyList<string> fallbackColumns)
    {
        var layout = ReportLayoutDefinitionParser.ParseOrDefault(layoutJson);

        if (layout.Columns is { Count: > 0 })
        {
            var directColumns = layout.Columns
                .Where(column => !string.IsNullOrWhiteSpace(column.Field))
                .Select(column => new ReportColumnProjection(column.Field!, ResolveHeader(column), column.Format))
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

                    sectionColumns.Add(new ReportColumnProjection(column.Field, ResolveHeader(column), column.Format));
                }
            }

            if (sectionColumns.Count > 0)
                return sectionColumns;
        }

        return fallbackColumns.Select(column => new ReportColumnProjection(column, column, null)).ToList();
    }

    private static string ResolveHeader(ReportLayoutColumnDefinition column)
    {
        var header = string.IsNullOrWhiteSpace(column.DisplayHeader) ? column.Field : column.DisplayHeader;
        return string.IsNullOrWhiteSpace(header) ? string.Empty : header;
    }

    private sealed record ReportColumnProjection(string Field, string Header, string? Format);

    /// <summary>
    /// Neutraliza formula injection: Excel/LibreOffice interpretam campos que
    /// comecam com =, +, @ (ou tab/CR) como formula, mesmo entre aspas. Numeros
    /// negativos (que comecam com -) sao preservados como numero.
    /// </summary>
    private static string NeutralizeFormula(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var first = value[0];
        if (first is '=' or '+' or '@' or '\t' or '\r')
            return "'" + value;

        if (first == '-' && !decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return "'" + value;

        return value;
    }

    private static string Escape(string value)
    {
        var normalized = value
            .Replace("\r\n", " ")
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\"", "\"\"");

        return $"\"{normalized}\"";
    }
}

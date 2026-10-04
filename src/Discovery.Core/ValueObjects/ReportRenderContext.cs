namespace Discovery.Core.ValueObjects;

public class ReportRenderContext
{
    public required string TemplateName { get; init; }
    public string? LayoutJson { get; init; }

    public string Title => ReportLayoutDefinitionParser.ParseOrDefault(LayoutJson).Title ?? TemplateName;
    public string? Subtitle => ReportLayoutDefinitionParser.ParseOrDefault(LayoutJson).Subtitle;
}

public class ReportPreviewResult
{
    public required ReportDocument Document { get; init; }
    public required int RowCount { get; init; }
    public required string Title { get; init; }
}

public class ReportHtmlPreviewResult
{
    public required string Html { get; init; }
    public required int RowCount { get; init; }
    public required string Title { get; init; }
}

/// <summary>
/// Resultado do download de uma execucao de relatorio. O conteudo e entregue
/// pelo proprio servidor (stream), valido para qualquer provedor de storage.
/// </summary>
public class ReportDownloadResult
{
    public required Stream Content { get; init; }
    public required string ContentType { get; init; }
    public required string FileName { get; init; }
    public long? SizeBytes { get; init; }
}
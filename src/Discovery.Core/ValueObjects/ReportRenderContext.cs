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
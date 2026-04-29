using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Enums;

namespace Discovery.Core.ValueObjects;

public class ReportLayoutDefinition
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Orientation { get; init; }
    public string? LogoUrl { get; init; }
    public string? GroupBy { get; init; }
    public string? GroupTitleTemplate { get; init; }
    public string? GroupTitlePrefix { get; init; }
    public bool HideGroupColumn { get; init; }
    public List<ReportLayoutColumnDefinition>? GroupDetails { get; init; }
    public List<ReportLayoutColumnDefinition>? Columns { get; init; }
    public List<ReportLayoutSectionDefinition>? Sections { get; init; }
    public List<ReportLayoutSummaryDefinition>? Summaries { get; init; }
    public List<ReportLayoutSummaryDefinition>? GroupSummaries { get; init; }
    public List<ReportLayoutDataSourceDefinition>? DataSources { get; init; }
    public ReportLayoutStyleDefinition? Style { get; init; }
    public ReportLayoutCoverPageDefinition? CoverPage { get; init; }
    public ReportLayoutPageHeaderFooterDefinition? PageHeader { get; init; }
    public ReportLayoutPageHeaderFooterDefinition? PageFooter { get; init; }
    public ReportLayoutTableOfContentsDefinition? TableOfContents { get; init; }
    public ReportLayoutWatermarkDefinition? Watermark { get; init; }
    public List<ReportLayoutChartDefinition>? Charts { get; init; }
    public List<ReportLayoutComputedFieldDefinition>? ComputedFields { get; init; }

    public static ReportLayoutDefinition Empty { get; } = new();
}

public class ReportLayoutSectionDefinition
{
    public string? Title { get; init; }
    public List<ReportLayoutColumnDefinition>? Columns { get; init; }
}

public class ReportLayoutColumnDefinition
{
    public string? Field { get; init; }
    public string? Header { get; init; }
    public string? Label { get; init; }
    public string? Format { get; init; }
    public JsonElement? Width { get; init; }
    public ReportLayoutConditionalFormat? ConditionalFormat { get; init; }

    [JsonIgnore]
    public string? DisplayHeader => string.IsNullOrWhiteSpace(Header) ? Label : Header;
}

public class ReportLayoutSummaryDefinition
{
    public string? Label { get; init; }
    public string? Field { get; init; }
    public string? Aggregate { get; init; }
    public string? Format { get; init; }
}

public class ReportLayoutStyleDefinition
{
    public string? PrimaryColor { get; init; }
    public string? SecondaryColor { get; init; }
    public string? HeaderBackgroundColor { get; init; }
    public string? HeaderTextColor { get; init; }
    public string? AlternateRowColor { get; init; }
    public string? BorderColor { get; init; }
    public string? FontFamily { get; init; }
    public string? LogoUrl { get; init; }
    public int? LogoMaxHeightPx { get; init; }
}

public static class ReportLayoutDefinitionParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ReportLayoutDefinition ParseOrDefault(string? layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson))
            return ReportLayoutDefinition.Empty;

        try
        {
            return JsonSerializer.Deserialize<ReportLayoutDefinition>(layoutJson, JsonOptions) ?? ReportLayoutDefinition.Empty;
        }
        catch
        {
            return ReportLayoutDefinition.Empty;
        }
    }
}

public class ReportLayoutDataSourceDefinition
{
    public ReportDatasetType? DatasetType { get; init; }
    public string? Alias { get; init; }
    public JsonElement? Filters { get; init; }
    public ReportLayoutDataSourceJoinDefinition? Join { get; init; }
}

public class ReportLayoutDataSourceJoinDefinition
{
    public string? JoinToAlias { get; init; }
    public string? SourceKey { get; init; }
    public string? TargetKey { get; init; }
    public string? JoinType { get; init; }
}

public class ReportLayoutCoverPageDefinition
{
    public bool Enabled { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? LogoUrl { get; init; }
    public bool ShowParameters { get; init; }
    public bool ShowGeneratedAt { get; init; } = true;
    public bool ShowRowCount { get; init; }
}

public class ReportLayoutPageHeaderFooterDefinition
{
    public string? Left { get; init; }
    public string? Center { get; init; }
    public string? Right { get; init; }
}

public class ReportLayoutTableOfContentsDefinition
{
    public bool Enabled { get; init; }
    public string? Title { get; init; }
    public int MaxLevel { get; init; } = 2;
}

public class ReportLayoutWatermarkDefinition
{
    public string? Text { get; init; }
    public string? Color { get; init; }
    public int FontSize { get; init; } = 120;
    public int Angle { get; init; } = -45;
    public bool Repeat { get; init; }
}

public class ReportLayoutChartDefinition
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public int Width { get; init; } = 800;
    public int Height { get; init; } = 400;
    public string? GroupField { get; init; }
    public string? ValueField { get; init; }
    public string? Aggregate { get; init; }
    public int Limit { get; init; } = 10;
    public string? Orientation { get; init; }
    public string? ValueExpr { get; init; }
    public List<ReportLayoutChartThreshold>? Thresholds { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? BucketBy { get; init; }
}

public class ReportLayoutChartThreshold
{
    public double Value { get; init; }
    public string? Color { get; init; }
}

public class ReportLayoutComputedFieldDefinition
{
    public string? Name { get; init; }
    public string? Expression { get; init; }
    public string? Format { get; init; }
}

public class ReportLayoutConditionalFormatRule
{
    public string? Operator { get; init; }
    public object? Value { get; init; }
    public string? BackgroundColor { get; init; }
    public string? TextColor { get; init; }
    public string? Icon { get; init; }
    public string? Label { get; init; }
}

public class ReportLayoutConditionalFormat
{
    public List<ReportLayoutConditionalFormatRule>? Rules { get; init; }
}
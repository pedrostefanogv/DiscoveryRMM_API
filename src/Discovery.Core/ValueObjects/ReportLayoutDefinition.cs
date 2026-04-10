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
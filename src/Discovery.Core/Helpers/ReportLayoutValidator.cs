using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Helpers;

public static partial class ReportLayoutValidator
{
    private const int MaxLayoutJsonLength = 64_000;
    private const int MaxTitleLength = 200;
    private const int MaxSubtitleLength = 500;
    private const int MaxGroupFieldLength = 120;
    private const int MaxGroupTitleLength = 200;
    private const int MaxSections = 12;
    private const int MaxColumnsPerSection = 20;
    private const int MaxTopLevelColumns = 24;
    private const int MaxGroupDetails = 12;
    private const int MaxSummaries = 12;
    private const int MaxDataSources = 8;
    private const int MaxLogoUrlLength = 8_192;
    private const int MaxDataUrlLength = 32_768;
    private static readonly HashSet<string> AllowedOrientations = new(StringComparer.OrdinalIgnoreCase) { "portrait", "landscape" };
    private static readonly HashSet<string> AllowedColumnFormats = new(StringComparer.OrdinalIgnoreCase) { "text", "date", "datetime", "number", "boolean" };
    private static readonly HashSet<string> AllowedAggregates = new(StringComparer.OrdinalIgnoreCase) { "count", "countDistinct", "sum", "avg", "min", "max", "countIf", "sumIf", "first", "last", "median", "percentile90", "compliancePercent" };
    private static readonly HashSet<string> AllowedJoinTypes = new(StringComparer.OrdinalIgnoreCase) { "left", "inner" };
    private static readonly HashSet<string> AllowedWatermarkFits = new(StringComparer.OrdinalIgnoreCase) { "contain", "cover" };
    private static readonly HashSet<string> AllowedChartTypes = new(StringComparer.OrdinalIgnoreCase) { "bar", "horizontalbar", "pie", "doughnut", "line", "stackedbar", "gauge", "sparkline" };
    private static readonly HashSet<string> AllowedConditionalOperators = new(StringComparer.OrdinalIgnoreCase) { "eq", "ne", "lt", "lte", "gt", "gte", "contains" };
    private static readonly HashSet<string> AllowedChartAggregates = new(StringComparer.OrdinalIgnoreCase) { "count", "countDistinct", "sum", "avg", "min", "max", "compliancePercent" };
    private static readonly HashSet<string> AllowedBucketByIntervals = new(StringComparer.OrdinalIgnoreCase) { "hour", "day", "week", "month" };

    public static IReadOnlyCollection<string> GetSupportedOrientations() => AllowedOrientations.ToArray();
    public static IReadOnlyCollection<string> GetSupportedColumnFormats() => AllowedColumnFormats.ToArray();
    public static IReadOnlyCollection<string> GetSupportedSummaryAggregates() => AllowedAggregates.ToArray();
    public static object GetLimits() => new
    {
        maxLayoutJsonLength = MaxLayoutJsonLength,
        maxSections = MaxSections,
        maxColumnsPerSection = MaxColumnsPerSection,
        maxTopLevelColumns = MaxTopLevelColumns,
        maxGroupDetails = MaxGroupDetails,
        maxSummaries = MaxSummaries,
        maxDataSources = MaxDataSources,
        maxLogoUrlLength = MaxLogoUrlLength,
        maxDataUrlLength = MaxDataUrlLength
    };

    public static IReadOnlyList<string> ValidateJson(string? layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson))
            return ["LayoutJson is required."];

        if (layoutJson.Length > MaxLayoutJsonLength)
            return [$"LayoutJson exceeds maximum size of {MaxLayoutJsonLength} characters."];

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(layoutJson);
            root = document.RootElement.Clone();
        }
        catch
        {
            return ["LayoutJson must be a valid JSON object."];
        }

        if (root.ValueKind != JsonValueKind.Object)
            return ["LayoutJson must be a JSON object."];

        var layout = ReportLayoutDefinitionParser.ParseOrDefault(layoutJson);
        return Validate(layout);
    }

    public static IReadOnlyList<string> Validate(ReportLayoutDefinition layout)
    {
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(layout.Title) && layout.Title.Length > MaxTitleLength)
            errors.Add($"Layout title exceeds maximum length of {MaxTitleLength}.");

        if (!string.IsNullOrWhiteSpace(layout.Subtitle) && layout.Subtitle.Length > MaxSubtitleLength)
            errors.Add($"Layout subtitle exceeds maximum length of {MaxSubtitleLength}.");

        if (!string.IsNullOrWhiteSpace(layout.Orientation) && !AllowedOrientations.Contains(layout.Orientation))
            errors.Add("Layout orientation must be 'portrait' or 'landscape'.");

        if (!string.IsNullOrWhiteSpace(layout.GroupBy) && layout.GroupBy.Length > MaxGroupFieldLength)
            errors.Add($"groupBy exceeds maximum length of {MaxGroupFieldLength}.");

        if (!string.IsNullOrWhiteSpace(layout.GroupTitleTemplate) && layout.GroupTitleTemplate.Length > MaxGroupTitleLength)
            errors.Add($"groupTitleTemplate exceeds maximum length of {MaxGroupTitleLength}.");

        if (!string.IsNullOrWhiteSpace(layout.GroupTitlePrefix) && layout.GroupTitlePrefix.Length > MaxGroupTitleLength)
            errors.Add($"groupTitlePrefix exceeds maximum length of {MaxGroupTitleLength}.");

        if (layout.GroupDetails is { Count: > 0 } && layout.GroupDetails.Count > MaxGroupDetails)
            errors.Add($"groupDetails supports at most {MaxGroupDetails} items.");

        ValidateLogo(layout.LogoUrl, "logoUrl", errors);
        ValidateStyle(layout.Style, errors);
        ValidateWatermark(layout.Watermark, errors);

        if (layout.Columns is { Count: > 0 } && layout.Columns.Count > MaxTopLevelColumns)
            errors.Add($"Layout supports at most {MaxTopLevelColumns} top-level columns.");

        if (layout.Sections is { Count: > 0 } && layout.Sections.Count > MaxSections)
            errors.Add($"Layout supports at most {MaxSections} sections.");

        if (layout.Columns is { Count: > 0 })
            ValidateColumns(layout.Columns, "columns", errors);

        if (layout.GroupDetails is { Count: > 0 })
            ValidateColumns(layout.GroupDetails, "groupDetails", errors);

        if (layout.Sections is { Count: > 0 })
        {
            for (var index = 0; index < layout.Sections.Count; index++)
            {
                var section = layout.Sections[index];
                if (!string.IsNullOrWhiteSpace(section.Title) && section.Title!.Length > MaxTitleLength)
                    errors.Add($"sections[{index}].title exceeds maximum length of {MaxTitleLength}.");

                if (section.Columns is not { Count: > 0 })
                {
                    errors.Add($"sections[{index}] must declare at least one column.");
                    continue;
                }

                if (section.Columns.Count > MaxColumnsPerSection)
                    errors.Add($"sections[{index}] supports at most {MaxColumnsPerSection} columns.");

                ValidateColumns(section.Columns, $"sections[{index}].columns", errors);
            }
        }

        ValidateSummaries(layout.Summaries, "summaries", errors);
        ValidateSummaries(layout.GroupSummaries, "groupSummaries", errors);
        ValidateDataSources(layout.DataSources, errors);
        ValidateCharts(layout.Charts, errors);
        ValidateComputedFields(layout.ComputedFields, errors);
        ValidateGroupLevels(layout.GroupLevels, errors);
        ValidateCoverPage(layout.CoverPage, errors);
        ValidatePageHeaderFooter(layout.PageHeader, "pageHeader", errors);
        ValidatePageHeaderFooter(layout.PageFooter, "pageFooter", errors);
        ValidateTableOfContents(layout.TableOfContents, errors);

        return errors;
    }

    private static void ValidateDataSources(IReadOnlyList<ReportLayoutDataSourceDefinition>? dataSources, ICollection<string> errors)
    {
        if (dataSources is not { Count: > 0 })
            return;

        if (dataSources.Count > MaxDataSources)
            errors.Add($"dataSources supports at most {MaxDataSources} sources.");

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < dataSources.Count; index++)
        {
            var source = dataSources[index];

            if (!source.DatasetType.HasValue)
                errors.Add($"dataSources[{index}].datasetType is required.");

            if (string.IsNullOrWhiteSpace(source.Alias))
            {
                errors.Add($"dataSources[{index}].alias is required.");
            }
            else
            {
                if (source.Alias.Length > 80)
                    errors.Add($"dataSources[{index}].alias exceeds maximum length of 80.");

                if (!AliasRegex().IsMatch(source.Alias))
                    errors.Add($"dataSources[{index}].alias must contain only letters, numbers and underscore.");

                if (!aliases.Add(source.Alias))
                    errors.Add($"dataSources[{index}].alias '{source.Alias}' is duplicated.");
            }

            if (index == 0)
                continue;

            if (source.Join is null)
            {
                errors.Add($"dataSources[{index}].join is required from second source onward.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(source.Join.SourceKey))
                errors.Add($"dataSources[{index}].join.sourceKey is required.");

            if (!string.IsNullOrWhiteSpace(source.Join.JoinType) && !AllowedJoinTypes.Contains(source.Join.JoinType))
                errors.Add($"dataSources[{index}].join.joinType must be one of: {string.Join(", ", AllowedJoinTypes)}.");

            if (!string.IsNullOrWhiteSpace(source.Join.JoinToAlias) && !aliases.Contains(source.Join.JoinToAlias))
                errors.Add($"dataSources[{index}].join.joinToAlias references unknown alias '{source.Join.JoinToAlias}'.");
        }
    }

    private static void ValidateColumns(IReadOnlyList<ReportLayoutColumnDefinition> columns, string path, ICollection<string> errors)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            if (string.IsNullOrWhiteSpace(column.Field))
                errors.Add($"{path}[{index}].field is required.");
            else if (column.Field.Length > MaxGroupFieldLength)
                errors.Add($"{path}[{index}].field exceeds maximum length of {MaxGroupFieldLength}.");

            var displayHeader = string.IsNullOrWhiteSpace(column.Header) ? column.Label : column.Header;
            if (!string.IsNullOrWhiteSpace(displayHeader) && displayHeader!.Length > MaxTitleLength)
                errors.Add($"{path}[{index}].header/label exceeds maximum length of {MaxTitleLength}.");

            if (!string.IsNullOrWhiteSpace(column.Format) && !AllowedColumnFormats.Contains(column.Format))
                errors.Add($"{path}[{index}].format must be one of: {string.Join(", ", AllowedColumnFormats)}.");

            if (column.Width.HasValue)
                ValidateWidth(column.Width.Value, path, index, errors);

            if (column.ConditionalFormat?.Rules is { Count: > 0 })
                ValidateConditionalFormat(column.ConditionalFormat, $"{path}[{index}].conditionalFormat", errors);
        }
    }

    private static void ValidateWidth(JsonElement width, string path, int index, ICollection<string> errors)
    {
        switch (width.ValueKind)
        {
            case JsonValueKind.Number:
                if (!width.TryGetInt32(out var numericWidth) || numericWidth < 1 || numericWidth > 100)
                    errors.Add($"{path}[{index}].width number must be between 1 and 100.");
                return;

            case JsonValueKind.String:
                var raw = width.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    return;

                var value = raw.Trim();
                if (value.EndsWith("%", StringComparison.Ordinal))
                {
                    var percentPart = value.Substring(0, value.Length - 1);
                    if (!int.TryParse(percentPart, out var percent) || percent < 1 || percent > 100)
                        errors.Add($"{path}[{index}].width percentage must be between 1% and 100%.");
                    return;
                }

                if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                {
                    var pxPart = value.Substring(0, value.Length - 2);
                    if (!int.TryParse(pxPart, out var px) || px < 1 || px > 2000)
                        errors.Add($"{path}[{index}].width px must be between 1px and 2000px.");
                    return;
                }

                if (!int.TryParse(value, out var plainNumber) || plainNumber < 1 || plainNumber > 100)
                    errors.Add($"{path}[{index}].width string must be numeric (1-100), percentage (1%-100%) or px (1px-2000px).");
                return;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return;

            default:
                errors.Add($"{path}[{index}].width must be a number or string.");
                return;
        }
    }

    private static void ValidateSummaries(IReadOnlyList<ReportLayoutSummaryDefinition>? summaries, string path, ICollection<string> errors)
    {
        if (summaries is not { Count: > 0 })
            return;

        if (summaries.Count > MaxSummaries)
        {
            errors.Add($"{path} supports at most {MaxSummaries} summary items.");
            return;
        }

        for (var index = 0; index < summaries.Count; index++)
        {
            var summary = summaries[index];
            if (!string.IsNullOrWhiteSpace(summary.Label) && summary.Label!.Length > MaxTitleLength)
                errors.Add($"{path}[{index}].label exceeds maximum length of {MaxTitleLength}.");

            if (string.IsNullOrWhiteSpace(summary.Aggregate) || !AllowedAggregates.Contains(summary.Aggregate))
                errors.Add($"{path}[{index}].aggregate must be one of: {string.Join(", ", AllowedAggregates)}.");

            if (!string.Equals(summary.Aggregate, "count", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(summary.Field))
                errors.Add($"{path}[{index}].field is required for aggregate '{summary.Aggregate}'.");

            if (!string.IsNullOrWhiteSpace(summary.Field) && summary.Field!.Length > MaxGroupFieldLength)
                errors.Add($"{path}[{index}].field exceeds maximum length of {MaxGroupFieldLength}.");

            if (!string.IsNullOrWhiteSpace(summary.Format) && !AllowedColumnFormats.Contains(summary.Format))
                errors.Add($"{path}[{index}].format must be one of: {string.Join(", ", AllowedColumnFormats)}.");
        }
    }

    private static void ValidateStyle(ReportLayoutStyleDefinition? style, ICollection<string> errors)
    {
        if (style is null)
            return;

        ValidateColor(style.PrimaryColor, "style.primaryColor", errors);
        ValidateColor(style.SecondaryColor, "style.secondaryColor", errors);
        ValidateColor(style.HeaderBackgroundColor, "style.headerBackgroundColor", errors);
        ValidateColor(style.HeaderTextColor, "style.headerTextColor", errors);
        ValidateColor(style.AlternateRowColor, "style.alternateRowColor", errors);
        ValidateColor(style.BorderColor, "style.borderColor", errors);
        ValidateLogo(style.LogoUrl, "style.logoUrl", errors);

        if (!string.IsNullOrWhiteSpace(style.FontFamily) && style.FontFamily!.Length > 120)
            errors.Add("style.fontFamily exceeds maximum length of 120.");

        if (style.LogoMaxHeightPx.HasValue && (style.LogoMaxHeightPx < 24 || style.LogoMaxHeightPx > 200))
            errors.Add("style.logoMaxHeightPx must be between 24 and 200.");
    }

    private static void ValidateWatermark(ReportLayoutWatermarkDefinition? watermark, ICollection<string> errors)
    {
        if (watermark is null)
            return;

        ValidateLogo(watermark.LogoUrl, "watermark.logoUrl", errors);
        ValidateLogo(watermark.ImageUrl, "watermark.imageUrl", errors);

        if (!string.IsNullOrWhiteSpace(watermark.ImageFit) && !AllowedWatermarkFits.Contains(watermark.ImageFit))
            errors.Add($"watermark.imageFit must be one of: {string.Join(", ", AllowedWatermarkFits)}.");
    }

    private static void ValidateColor(string? value, string fieldName, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!HexColorRegex().IsMatch(value))
            errors.Add($"{fieldName} must be a hex color like #16324F.");
    }

    private static void ValidateLogo(string? value, string fieldName, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (value.Length > MaxLogoUrlLength)
        {
            errors.Add($"{fieldName} exceeds maximum length of {MaxLogoUrlLength}.");
            return;
        }

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Length > MaxDataUrlLength)
                errors.Add($"{fieldName} data URL exceeds maximum length of {MaxDataUrlLength}.");

            if (!value.StartsWith("data:image/png", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("data:image/webp", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{fieldName} data URL must be PNG, JPEG, SVG or WEBP.");
            }

            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{fieldName} must be an absolute http/https URL or a supported data URL.");
        }
    }

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColorRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex AliasRegex();

    private static void ValidateCharts(IReadOnlyList<ReportLayoutChartDefinition>? charts, ICollection<string> errors)
    {
        if (charts is not { Count: > 0 })
            return;

        const int maxCharts = 16;
        if (charts.Count > maxCharts)
            errors.Add($"charts supports at most {maxCharts} items.");

        for (var i = 0; i < charts.Count; i++)
        {
            var chart = charts[i];
            if (string.IsNullOrWhiteSpace(chart.Type) || !AllowedChartTypes.Contains(chart.Type))
                errors.Add($"charts[{i}].type must be one of: {string.Join(", ", AllowedChartTypes)}.");

            if (!string.IsNullOrWhiteSpace(chart.Aggregate) && !AllowedChartAggregates.Contains(chart.Aggregate))
                errors.Add($"charts[{i}].aggregate must be one of: {string.Join(", ", AllowedChartAggregates)}.");

            if (chart.Type is not null && chart.Type != "gauge" && string.IsNullOrWhiteSpace(chart.GroupField))
                errors.Add($"charts[{i}].groupField is required for chart type '{chart.Type}'.");

            if (!string.IsNullOrWhiteSpace(chart.BucketBy) && !AllowedBucketByIntervals.Contains(chart.BucketBy))
                errors.Add($"charts[{i}].bucketBy must be one of: {string.Join(", ", AllowedBucketByIntervals)}.");

            if (chart.Limit is < 1 or > 100)
                errors.Add($"charts[{i}].limit must be between 1 and 100.");

            if (chart.Thresholds is { Count: > 0 })
            {
                for (var t = 0; t < chart.Thresholds.Count; t++)
                {
                    var threshold = chart.Thresholds[t];
                    if (!string.IsNullOrWhiteSpace(threshold.Color))
                        ValidateColor(threshold.Color, $"charts[{i}].thresholds[{t}].color", errors);
                }
            }
        }
    }

    private static void ValidateComputedFields(IReadOnlyList<ReportLayoutComputedFieldDefinition>? computedFields, ICollection<string> errors)
    {
        if (computedFields is not { Count: > 0 })
            return;

        const int maxComputedFields = 20;
        if (computedFields.Count > maxComputedFields)
            errors.Add($"computedFields supports at most {maxComputedFields} items.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < computedFields.Count; i++)
        {
            var field = computedFields[i];
            if (string.IsNullOrWhiteSpace(field.Name))
                errors.Add($"computedFields[{i}].name is required.");
            else if (field.Name.Length > MaxGroupFieldLength)
                errors.Add($"computedFields[{i}].name exceeds maximum length of {MaxGroupFieldLength}.");
            else if (!names.Add(field.Name))
                errors.Add($"computedFields[{i}].name '{field.Name}' is duplicated.");

            if (string.IsNullOrWhiteSpace(field.Expression))
                errors.Add($"computedFields[{i}].expression is required.");
            else if (field.Expression.Length > 512)
                errors.Add($"computedFields[{i}].expression exceeds maximum length of 512.");

            if (!string.IsNullOrWhiteSpace(field.Format) && !AllowedColumnFormats.Contains(field.Format))
                errors.Add($"computedFields[{i}].format must be one of: {string.Join(", ", AllowedColumnFormats)}.");
        }
    }

    private static void ValidateCoverPage(ReportLayoutCoverPageDefinition? coverPage, ICollection<string> errors)
    {
        if (coverPage is null || !coverPage.Enabled)
            return;

        if (!string.IsNullOrWhiteSpace(coverPage.Title) && coverPage.Title.Length > MaxTitleLength)
            errors.Add("coverPage.title exceeds maximum length of {MaxTitleLength}.");

        if (!string.IsNullOrWhiteSpace(coverPage.Subtitle) && coverPage.Subtitle.Length > MaxSubtitleLength)
            errors.Add("coverPage.subtitle exceeds maximum length of {MaxSubtitleLength}.");

        ValidateLogo(coverPage.LogoUrl, "coverPage.logoUrl", errors);
    }

    private static void ValidatePageHeaderFooter(ReportLayoutPageHeaderFooterDefinition? definition, string path, ICollection<string> errors)
    {
        if (definition is null)
            return;

        if (!string.IsNullOrWhiteSpace(definition.Left) && definition.Left.Length > MaxGroupFieldLength)
            errors.Add($"{path}.left exceeds maximum length of {MaxGroupFieldLength}.");
        if (!string.IsNullOrWhiteSpace(definition.Center) && definition.Center.Length > MaxGroupFieldLength)
            errors.Add($"{path}.center exceeds maximum length of {MaxGroupFieldLength}.");
        if (!string.IsNullOrWhiteSpace(definition.Right) && definition.Right.Length > MaxGroupFieldLength)
            errors.Add($"{path}.right exceeds maximum length of {MaxGroupFieldLength}.");
    }

    private static void ValidateTableOfContents(ReportLayoutTableOfContentsDefinition? toc, ICollection<string> errors)
    {
        if (toc is null || !toc.Enabled)
            return;

        if (!string.IsNullOrWhiteSpace(toc.Title) && toc.Title.Length > MaxTitleLength)
            errors.Add("tableOfContents.title exceeds maximum length of {MaxTitleLength}.");

        if (toc.MaxLevel is < 1 or > 3)
            errors.Add("tableOfContents.maxLevel must be between 1 and 3.");
    }

    private static void ValidateConditionalFormat(ReportLayoutConditionalFormat conditionalFormat, string path, ICollection<string> errors)
    {
        if (conditionalFormat.Rules is not { Count: > 0 })
            return;

        const int maxRules = 20;
        if (conditionalFormat.Rules.Count > maxRules)
            errors.Add($"{path}.rules supports at most {maxRules} items.");

        for (var i = 0; i < conditionalFormat.Rules.Count; i++)
        {
            var rule = conditionalFormat.Rules[i];
            if (string.IsNullOrWhiteSpace(rule.Operator) || !AllowedConditionalOperators.Contains(rule.Operator))
                errors.Add($"{path}.rules[{i}].operator must be one of: {string.Join(", ", AllowedConditionalOperators)}.");

            if (!string.IsNullOrWhiteSpace(rule.BackgroundColor))
                ValidateColor(rule.BackgroundColor, $"{path}.rules[{i}].backgroundColor", errors);

            if (!string.IsNullOrWhiteSpace(rule.TextColor))
                ValidateColor(rule.TextColor, $"{path}.rules[{i}].textColor", errors);

            if (!string.IsNullOrWhiteSpace(rule.Label) && rule.Label.Length > MaxTitleLength)
                errors.Add($"{path}.rules[{i}].label exceeds maximum length of {MaxTitleLength}.");
        }
    }

    private static void ValidateGroupLevels(IReadOnlyList<ReportLayoutGroupLevelDefinition>? groupLevels, ICollection<string> errors)
    {
        if (groupLevels is not { Count: > 0 })
            return;

        const int maxGroupLevels = 5;
        if (groupLevels.Count > maxGroupLevels)
            errors.Add($"groupLevels supports at most {maxGroupLevels} levels.");

        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < groupLevels.Count; i++)
        {
            var level = groupLevels[i];
            if (string.IsNullOrWhiteSpace(level.Field))
                errors.Add($"groupLevels[{i}].field is required.");
            else if (level.Field.Length > MaxGroupFieldLength)
                errors.Add($"groupLevels[{i}].field exceeds maximum length of {MaxGroupFieldLength}.");
            else if (!fields.Add(level.Field))
                errors.Add($"groupLevels[{i}].field '{level.Field}' is duplicated.");

            if (!string.IsNullOrWhiteSpace(level.TitleTemplate) && level.TitleTemplate.Length > MaxGroupTitleLength)
                errors.Add($"groupLevels[{i}].titleTemplate exceeds maximum length of {MaxGroupTitleLength}.");
        }
    }
}
using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Reports.Queries;

// --- Layout schema (orientacoes, formatos, agregacoes, limites, multi-source) ---
public sealed record GetReportLayoutSchemaQuery() : IQuery<Result<ReportLayoutSchemaDto>>;

public sealed record ReportLayoutSchemaDataSourceDto(
    int DatasetType,
    string DatasetName,
    string DefaultAlias,
    string? Description);

public sealed record ReportLayoutJoinRuleDto(
    int SourceDatasetType,
    int TargetDatasetType,
    string SourceKey,
    string TargetKey,
    string JoinType,
    string? Description);

public sealed record ReportLayoutSchemaMultiSourceDto(
    bool Enabled,
    string FieldReferenceMode,
    IReadOnlyList<ReportLayoutSchemaDataSourceDto> DataSources,
    IReadOnlyList<string> JoinTypes,
    IReadOnlyList<ReportLayoutJoinRuleDto> JoinRules,
    IReadOnlyList<string> Notes);

public sealed record ReportLayoutSchemaLimitsDto(
    int MaxLayoutJsonLength,
    int MaxColumns,
    int MaxSections,
    int MaxSectionColumns,
    int MaxSummaries,
    int MaxGroupDetails);

public sealed record ReportLayoutSchemaDto(
    IReadOnlyList<string> PreviewModes,
    IReadOnlyList<string> ResponseDispositions,
    IReadOnlyList<string> SupportedOrientations,
    IReadOnlyList<string> SupportedColumnFormats,
    IReadOnlyList<string> SupportedSummaryAggregates,
    ReportLayoutSchemaMultiSourceDto MultiSource,
    ReportLayoutSchemaLimitsDto Limits,
    IReadOnlyList<string> Notes);

// --- Join compatibility ---
public sealed record GetReportJoinCompatibilityQuery() : IQuery<Result<ReportJoinCompatibilityDto>>;

public sealed record ReportJoinSuggestionDto(
    string SourceA,
    string SourceB,
    IReadOnlyList<string> CommonKeys,
    string PreferredKey,
    string RecommendedJoinType);

public sealed record ReportJoinCompatibilityDto(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Compatibility,
    IReadOnlyList<ReportJoinSuggestionDto> JoinSuggestions,
    string Note);

// --- Field autocomplete ---
public sealed record GetReportFieldAutocompleteQuery(
    string? Term,
    int DatasetType,
    string? Alias = null) : IQuery<Result<ReportAutocompleteDto>>;

public sealed record ReportAutocompleteItemDto(
    int DatasetType,
    string DatasetKey,
    string? DatasetName,
    string Field,
    string Reference,
    string? DataType,
    bool IsJoinKey,
    string? DefaultAlias);

public sealed record ReportAutocompleteDto(
    string FieldReferenceMode,
    int Total,
    IReadOnlyList<ReportAutocompleteItemDto> Items);

// --- Template history ---
public sealed record GetReportTemplateHistoryQuery(Guid TemplateId, int Limit = 50)
    : IQuery<Result<IReadOnlyList<ReportTemplateHistoryDto>>>;

public sealed record ReportTemplateHistoryDto(
    Guid Id,
    Guid TemplateId,
    int Version,
    string EventType,
    string Name,
    int DatasetType,
    int DefaultFormat,
    string LayoutJson,
    string? FiltersJson,
    bool IsActive,
    DateTime CreatedAt,
    string? CreatedBy);

// --- Library ---
public sealed record ListReportLibraryQuery(int? DatasetType = null)
    : IQuery<Result<IReadOnlyList<ReportLibraryTemplateDto>>>;

public sealed record ReportLibraryTemplateDto(
    Guid Id,
    string Name,
    string? Description,
    int DatasetType,
    int DefaultFormat,
    string LayoutJson,
    string? FiltersJson,
    bool IsBuiltIn,
    string? Category,
    IReadOnlyList<string>? Tags,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record InstallReportLibraryTemplateCommand(
    Guid TemplateId,
    Guid? ClientId = null,
    string? CreatedBy = null) : ICommand<Result<ReportTemplateDto>>;

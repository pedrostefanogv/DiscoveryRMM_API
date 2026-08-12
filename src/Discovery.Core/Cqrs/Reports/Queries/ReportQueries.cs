using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Reports.Queries;

// --- Report Executions ---
public sealed record ListReportsQuery(Guid? ClientId) : IQuery<Result<IReadOnlyList<ReportDto>>>;
public sealed record GetReportExecutionQuery(Guid ExecutionId, Guid? ClientId) : IQuery<Result<ReportDto>>;

public sealed record ReportDto(Guid Id, string TemplateName, string Status, string Format, DateTime CreatedAt, DateTime? CompletedAt);

// --- Report Templates ---
public sealed record ListReportTemplatesQuery(Guid? ClientId = null, bool? IsActive = true) : IQuery<Result<IReadOnlyList<ReportTemplateDto>>>;
public sealed record GetReportTemplateByIdQuery(Guid Id, Guid? ClientId = null) : IQuery<Result<ReportTemplateDto>>;
public sealed record CreateReportTemplateCommand(
    Guid? ClientId,
    string Name,
    string? Description,
    string? Instructions,
    string? ExecutionSchemaJson,
    int DatasetType,
    int DefaultFormat,
    string? LayoutJson,
    string? FiltersJson) : ICommand<Result<ReportTemplateDto>>;
public sealed record UpdateReportTemplateCommand(
    Guid Id,
    Guid? ClientId,
    string? Name,
    string? Description,
    string? Instructions,
    string? ExecutionSchemaJson,
    int? DatasetType,
    int? DefaultFormat,
    string? LayoutJson,
    string? FiltersJson,
    bool? IsActive) : ICommand<Result<ReportTemplateDto>>;
public sealed record DeleteReportTemplateCommand(Guid Id, Guid? ClientId = null) : ICommand<Result<VoidResult>>;

public sealed record ReportTemplateDto(
    Guid Id,
    Guid? ClientId,
    string Name,
    string? Description,
    string? Instructions,
    int DatasetType,
    int DefaultFormat,
    bool IsActive,
    bool IsBuiltIn,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt);

// --- Report Run (RunNow) ---
public sealed record RunReportNowCommand(
    Guid TemplateId,
    int Format,
    string? FiltersJson = null,
    Guid? ClientId = null,
    Guid? ScheduleId = null) : ICommand<Result<ReportDto>>;

// --- Dataset Catalog ---
public sealed record GetReportDatasetCatalogQuery() : IQuery<Result<IReadOnlyList<ReportDatasetCatalogItemDto>>>;

public sealed record ReportDatasetFieldMetadataDto(
    string Field,
    string? Label = null,
    string? Reference = null,
    string? DataType = null,
    bool IsJoinKey = false,
    string? DefaultAlias = null,
    string? DatasetName = null,
    string? Description = null);

public sealed record ReportDatasetFilterDto(
    string Name,
    string Type,
    bool Required,
    string? Label = null);

public sealed record ReportDatasetJoinCapabilityDto(
    string SourceKey,
    string TargetKey,
    IReadOnlyList<string>? JoinTypes = null,
    string? Description = null);

public sealed record ReportDatasetCatalogItemDto(
    string Key,
    string Type,
    int DatasetType,
    string Name,
    string Description,
    IReadOnlyList<string> Fields,
    IReadOnlyList<ReportDatasetFieldMetadataDto> FieldMetadata,
    IReadOnlyList<ReportDatasetFilterDto> Filters,
    IReadOnlyList<ReportDatasetJoinCapabilityDto> JoinCapabilities,
    string DefaultFormat,
    IReadOnlyList<string> SupportedFormats);

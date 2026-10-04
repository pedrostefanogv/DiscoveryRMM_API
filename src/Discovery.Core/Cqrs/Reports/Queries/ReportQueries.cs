using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Reports.Queries;

// --- Report Executions ---
public sealed record ListReportsQuery(
    Guid? ClientId,
    int Limit = 50,
    // Para usuarios sem acesso global: lista restrita aos clientes permitidos.
    IReadOnlyList<Guid>? ForcedClientIds = null) : IQuery<Result<IReadOnlyList<ReportExecutionDto>>>;
public sealed record GetReportExecutionQuery(Guid ExecutionId, Guid? ClientId) : IQuery<Result<ReportExecutionDto>>;

/// <summary>
/// Resultado da execucao imediata (POST /reports/run).
/// O nome <c>executionId</c> casa com o contrato consumido pela UI; antes a
/// resposta devolvia <c>id</c> e a UI nao conseguia acompanhar nem baixar a
/// execucao recem-criada.
/// </summary>
public sealed record RunReportResultDto(
    Guid ExecutionId,
    int Status,
    int Format,
    int? RowCount,
    long? ResultSizeBytes,
    string? ContentType,
    string? DownloadPath,
    string TemplateName,
    DateTime CreatedAt,
    DateTime? FinishedAt);

/// <summary>
/// Projecao completa de uma execucao de relatorio.
/// Espelha o contrato consumido pela UI (ReportExecution): sem os campos de
/// resultado o polling de status e o download nao funcionavam (a UI recebia
/// apenas id/status/data).
/// </summary>
public sealed record ReportExecutionDto(
    Guid Id,
    Guid TemplateId,
    Guid? ClientId,
    int Format,
    string? FiltersJson,
    int Status,
    string? ResultPath,
    string? ResultContentType,
    long? ResultSizeBytes,
    int? RowCount,
    string? ErrorMessage,
    int? ExecutionTimeMs,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? CreatedBy,
    Guid? ScheduleId);

// --- Report Templates ---
/// <summary>
/// IsActive nulo = todos (inclui inativos); true = somente ativos. O default
/// passou de true para null para que a UI consiga expressar "mostrar inativos".
/// </summary>
public sealed record ListReportTemplatesQuery(Guid? ClientId = null, bool? IsActive = null, int? DatasetType = null) : IQuery<Result<IReadOnlyList<ReportTemplateDto>>>;
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
    string? FiltersJson,
    // Preenchido pelo controller a partir do usuario autenticado (nunca do body).
    string? CreatedBy = null) : ICommand<Result<ReportTemplateDto>>;
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
    bool? IsActive,
    // Preenchido pelo controller a partir do usuario autenticado (nunca do body).
    string? UpdatedBy = null) : ICommand<Result<ReportTemplateDto>>;
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
    DateTime UpdatedAt,
    // Necessarios para editar um template sem perder o conteudo configurado:
    // sem eles a UI reconstroi o formulario com valores default e sobrescreve
    // o layout/filtros originais.
    string LayoutJson,
    string? FiltersJson,
    string? ExecutionSchemaJson,
    string? CreatedBy,
    string? UpdatedBy);

// --- Report Run (RunNow) ---
public sealed record RunReportNowCommand(
    Guid TemplateId,
    int Format,
    string? FiltersJson = null,
    Guid? ClientId = null,
    Guid? ScheduleId = null,
    // Preenchido pelo controller a partir do usuario autenticado (nunca do body).
    string? CreatedBy = null,
    Guid? SiteId = null) : ICommand<Result<RunReportResultDto>>;

// --- Report Preview ---
public sealed record ReportPreviewTemplateInput(
    string Name,
    string? DatasetKey = null,
    object? DatasetType = null,
    string? Description = null,
    string? LayoutJson = null,
    string? FiltersJson = null);

public sealed record PreviewReportCommand(
    Guid? TemplateId = null,
    ReportPreviewTemplateInput? Template = null,
    object? Format = null,
    string? FiltersJson = null,
    string? PreviewMode = "document",
    string? ResponseDisposition = "inline",
    string? FileName = null,
    // Preenchidos pelo controller a partir do escopo resolvido (nunca confiar no body).
    Guid? ClientId = null,
    Guid? SiteId = null) : ICommand<Result<ReportPreviewResultDto>>;

public sealed record ReportPreviewResultDto(
    string Mode,
    string ContentType,
    int? RowCount,
    string? Title,
    string? Format,
    bool IsPreview,
    string? Disposition,
    string? Html = null,
    byte[]? Content = null);

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

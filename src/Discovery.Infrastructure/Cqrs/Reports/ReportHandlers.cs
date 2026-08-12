using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Reports.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Reports;

// existing
public sealed class ListReportsQueryHandler : IRequestHandler<ListReportsQuery, Result<IReadOnlyList<ReportDto>>>
{
    public Task<Result<IReadOnlyList<ReportDto>>> Handle(ListReportsQuery q, CancellationToken ct) => Task.FromResult(Result<IReadOnlyList<ReportDto>>.Success(Array.Empty<ReportDto>()));
}

// new — dataset catalog
public sealed class GetReportDatasetCatalogQueryHandler(IReportDatasetCatalogProvider provider)
    : IRequestHandler<GetReportDatasetCatalogQuery, Result<IReadOnlyList<ReportDatasetCatalogItemDto>>>
{
    public Task<Result<IReadOnlyList<ReportDatasetCatalogItemDto>>> Handle(GetReportDatasetCatalogQuery q, CancellationToken ct)
        => Task.FromResult(Result<IReadOnlyList<ReportDatasetCatalogItemDto>>.Success(provider.GetAll()));
}

public sealed class GetReportExecutionQueryHandler : IRequestHandler<GetReportExecutionQuery, Result<ReportDto>>
{
    public Task<Result<ReportDto>> Handle(GetReportExecutionQuery q, CancellationToken ct) => Task.FromResult(Result<ReportDto>.Failure(Error.NotFound($"Report {q.ExecutionId} not found")));
}

// new — templates list
public sealed class ListReportTemplatesQueryHandler(IReportTemplateRepository repo)
    : IRequestHandler<ListReportTemplatesQuery, Result<IReadOnlyList<ReportTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<ReportTemplateDto>>> Handle(ListReportTemplatesQuery q, CancellationToken ct)
    {
        var templates = await repo.GetAllAsync(q.ClientId, null, q.IsActive);
        var items = templates.Select(Map).ToList().AsReadOnly();
        return Result<IReadOnlyList<ReportTemplateDto>>.Success(items);
    }

    private static ReportTemplateDto Map(ReportTemplate t) => new(
        t.Id, t.ClientId, t.Name, t.Description, t.Instructions,
        (int)t.DatasetType, (int)t.DefaultFormat, t.IsActive, t.IsBuiltIn,
        t.Version, t.CreatedAt, t.UpdatedAt);
}

// new — template by id
public sealed class GetReportTemplateByIdQueryHandler(IReportTemplateRepository repo)
    : IRequestHandler<GetReportTemplateByIdQuery, Result<ReportTemplateDto>>
{
    public async Task<Result<ReportTemplateDto>> Handle(GetReportTemplateByIdQuery q, CancellationToken ct)
    {
        var t = await repo.GetByIdAsync(q.Id, q.ClientId);
        if (t is null)
            return Result<ReportTemplateDto>.Failure(Error.NotFound($"ReportTemplate {q.Id} not found"));
        return Result<ReportTemplateDto>.Success(new ReportTemplateDto(
            t.Id, t.ClientId, t.Name, t.Description, t.Instructions,
            (int)t.DatasetType, (int)t.DefaultFormat, t.IsActive, t.IsBuiltIn,
            t.Version, t.CreatedAt, t.UpdatedAt));
    }
}

// new — create template
public sealed class CreateReportTemplateCommandHandler(IReportTemplateRepository repo)
    : IRequestHandler<CreateReportTemplateCommand, Result<ReportTemplateDto>>
{
    public async Task<Result<ReportTemplateDto>> Handle(CreateReportTemplateCommand cmd, CancellationToken ct)
    {
        var template = new ReportTemplate
        {
            Id = Guid.NewGuid(),
            ClientId = cmd.ClientId,
            Name = cmd.Name,
            Description = cmd.Description,
            Instructions = cmd.Instructions,
            ExecutionSchemaJson = cmd.ExecutionSchemaJson,
            DatasetType = (ReportDatasetType)cmd.DatasetType,
            DefaultFormat = (ReportFormat)cmd.DefaultFormat,
            LayoutJson = cmd.LayoutJson ?? "{}",
            FiltersJson = cmd.FiltersJson,
            IsActive = true,
            IsBuiltIn = false,
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await repo.CreateAsync(template);
        return Result<ReportTemplateDto>.Success(new ReportTemplateDto(
            created.Id, created.ClientId, created.Name, created.Description,
            created.Instructions, (int)created.DatasetType, (int)created.DefaultFormat,
            created.IsActive, created.IsBuiltIn, created.Version,
            created.CreatedAt, created.UpdatedAt));
    }
}

// new — update template
public sealed class UpdateReportTemplateCommandHandler(IReportTemplateRepository repo)
    : IRequestHandler<UpdateReportTemplateCommand, Result<ReportTemplateDto>>
{
    public async Task<Result<ReportTemplateDto>> Handle(UpdateReportTemplateCommand cmd, CancellationToken ct)
    {
        var t = await repo.GetByIdAsync(cmd.Id, cmd.ClientId);
        if (t is null)
            return Result<ReportTemplateDto>.Failure(Error.NotFound($"ReportTemplate {cmd.Id} not found"));

        if (cmd.Name is not null) t.Name = cmd.Name;
        if (cmd.Description is not null) t.Description = cmd.Description;
        if (cmd.Instructions is not null) t.Instructions = cmd.Instructions;
        if (cmd.ExecutionSchemaJson is not null) t.ExecutionSchemaJson = cmd.ExecutionSchemaJson;
        if (cmd.DatasetType.HasValue) t.DatasetType = (ReportDatasetType)cmd.DatasetType.Value;
        if (cmd.DefaultFormat.HasValue) t.DefaultFormat = (ReportFormat)cmd.DefaultFormat.Value;
        if (cmd.LayoutJson is not null) t.LayoutJson = cmd.LayoutJson;
        if (cmd.FiltersJson is not null) t.FiltersJson = cmd.FiltersJson;
        if (cmd.IsActive.HasValue) t.IsActive = cmd.IsActive.Value;
        t.UpdatedAt = DateTime.UtcNow;
        t.Version++;

        await repo.UpdateAsync(t);
        return Result<ReportTemplateDto>.Success(new ReportTemplateDto(
            t.Id, t.ClientId, t.Name, t.Description, t.Instructions,
            (int)t.DatasetType, (int)t.DefaultFormat, t.IsActive, t.IsBuiltIn,
            t.Version, t.CreatedAt, t.UpdatedAt));
    }
}

// new — delete template
public sealed class DeleteReportTemplateCommandHandler(IReportTemplateRepository repo)
    : IRequestHandler<DeleteReportTemplateCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteReportTemplateCommand cmd, CancellationToken ct)
    {
        var deleted = await repo.DeleteAsync(cmd.Id, cmd.ClientId);
        if (!deleted)
            return Result<VoidResult>.Failure(Error.NotFound($"ReportTemplate {cmd.Id} not found"));
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

// new — run report now
public sealed class RunReportNowCommandHandler(IReportService reportService, IReportTemplateRepository templateRepo, IReportExecutionRepository execRepo)
    : IRequestHandler<RunReportNowCommand, Result<ReportDto>>
{
    public async Task<Result<ReportDto>> Handle(RunReportNowCommand cmd, CancellationToken ct)
    {
        var template = await templateRepo.GetByIdAsync(cmd.TemplateId, cmd.ClientId);
        if (template is null)
            return Result<ReportDto>.Failure(Error.NotFound($"ReportTemplate {cmd.TemplateId} not found"));

        var execution = new ReportExecution
        {
            Id = Guid.NewGuid(),
            TemplateId = cmd.TemplateId,
            ClientId = cmd.ClientId,
            Format = (ReportFormat)cmd.Format,
            FiltersJson = cmd.FiltersJson,
            Status = ReportExecutionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ScheduleId = cmd.ScheduleId
        };

        execution = await execRepo.CreateAsync(execution);
        execution = await reportService.ProcessExecutionAsync(execution.Id, cmd.ClientId, ct);

        return Result<ReportDto>.Success(new ReportDto(
            execution.Id, template.Name, execution.Status.ToString(),
            execution.Format.ToString(), execution.CreatedAt, execution.FinishedAt));
    }
}

// new — report preview
public sealed class PreviewReportCommandHandler(
    IReportService reportService,
    IReportTemplateRepository templateRepo)
    : IRequestHandler<PreviewReportCommand, Result<ReportPreviewResultDto>>
{
    public async Task<Result<ReportPreviewResultDto>> Handle(PreviewReportCommand cmd, CancellationToken ct)
    {
        var template = await ResolveTemplateAsync(cmd, ct);
        if (template is null)
            return Result<ReportPreviewResultDto>.Failure(Error.NotFound("ReportTemplate not found"));

        var isHtml = string.Equals(cmd.PreviewMode, "html", StringComparison.OrdinalIgnoreCase);

        if (isHtml)
        {
            var htmlResult = await reportService.PreviewHtmlAsync(template, cmd.FiltersJson, ct);
            return Result<ReportPreviewResultDto>.Success(new ReportPreviewResultDto(
                Mode: "html",
                ContentType: "text/html; charset=utf-8",
                RowCount: htmlResult.RowCount,
                Title: htmlResult.Title,
                Format: "html",
                IsPreview: true,
                Disposition: cmd.ResponseDisposition,
                Html: htmlResult.Html));
        }

        var format = cmd.Format.HasValue ? (ReportFormat)cmd.Format.Value : template.DefaultFormat;
        var result = await reportService.PreviewAsync(template, format, cmd.FiltersJson, ct);
        var contentType = format switch
        {
            ReportFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ReportFormat.Csv => "text/csv",
            ReportFormat.Markdown => "text/markdown",
            _ => "application/pdf"
        };

        return Result<ReportPreviewResultDto>.Success(new ReportPreviewResultDto(
            Mode: "document",
            ContentType: contentType,
            RowCount: result.RowCount,
            Title: result.Title,
            Format: format.ToString().ToLowerInvariant(),
            IsPreview: true,
            Disposition: cmd.ResponseDisposition,
            Content: result.Document.Content));
    }

    private async Task<ReportTemplate?> ResolveTemplateAsync(PreviewReportCommand cmd, CancellationToken ct)
    {
        if (cmd.TemplateId.HasValue)
            return await templateRepo.GetByIdAsync(cmd.TemplateId.Value, null);

        if (cmd.Template is null)
            return null;

        var input = cmd.Template;
        var datasetType = ResolveDatasetType(input.DatasetType, input.DatasetKey);
        if (datasetType is null)
            return null;

        return new ReportTemplate
        {
            Id = Guid.NewGuid(),
            Name = input.Name ?? "Preview",
            DatasetType = datasetType.Value,
            DefaultFormat = ReportFormat.Pdf,
            LayoutJson = input.LayoutJson ?? "{}",
            FiltersJson = input.FiltersJson,
            IsActive = true,
            IsBuiltIn = false,
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static ReportDatasetType? ResolveDatasetType(string? datasetType, string? datasetKey)
    {
        var raw = datasetType ?? datasetKey;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Número (ex: "4")
        if (int.TryParse(raw, out var numeric) && Enum.IsDefined(typeof(ReportDatasetType), numeric))
            return (ReportDatasetType)numeric;

        // Nome do enum (ex: "AgentHardware")
        if (Enum.TryParse<ReportDatasetType>(raw, ignoreCase: true, out var parsed))
            return parsed;

        // camelCase (ex: "agentHardware") → PascalCase
        var pascal = char.ToUpperInvariant(raw[0]) + raw[1..];
        if (Enum.TryParse<ReportDatasetType>(pascal, ignoreCase: true, out var parsedPascal))
            return parsedPascal;

        return null;
    }
}

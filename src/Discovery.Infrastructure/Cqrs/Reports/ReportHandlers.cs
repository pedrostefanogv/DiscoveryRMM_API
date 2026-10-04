using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Reports.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Reports;

// existing
public sealed class ListReportsQueryHandler(IReportExecutionRepository executions)
    : IRequestHandler<ListReportsQuery, Result<IReadOnlyList<ReportExecutionDto>>>
{
    public async Task<Result<IReadOnlyList<ReportExecutionDto>>> Handle(ListReportsQuery q, CancellationToken ct)
    {
        var items = q.ForcedClientIds is { Count: > 0 }
            ? await executions.GetRecentByClientIdsAsync(q.ForcedClientIds, q.Limit)
            : await executions.GetRecentByClientAsync(q.ClientId, q.Limit);
        return Result<IReadOnlyList<ReportExecutionDto>>.Success(items.Select(MapExecution).ToList());
    }

    internal static ReportExecutionDto MapExecution(ReportExecution execution) => new(
        execution.Id,
        execution.TemplateId,
        execution.ClientId,
        (int)execution.Format,
        execution.FiltersJson,
        (int)execution.Status,
        execution.StorageObjectKey,
        execution.StorageContentType,
        execution.StorageSizeBytes,
        execution.RowCount,
        execution.ErrorMessage,
        execution.ExecutionTimeMs,
        execution.CreatedAt,
        execution.StartedAt,
        execution.FinishedAt,
        execution.CreatedBy,
        execution.ScheduleId);
}

// new — dataset catalog
public sealed class GetReportDatasetCatalogQueryHandler(IReportDatasetCatalogProvider provider)
    : IRequestHandler<GetReportDatasetCatalogQuery, Result<IReadOnlyList<ReportDatasetCatalogItemDto>>>
{
    public Task<Result<IReadOnlyList<ReportDatasetCatalogItemDto>>> Handle(GetReportDatasetCatalogQuery q, CancellationToken ct)
        => Task.FromResult(Result<IReadOnlyList<ReportDatasetCatalogItemDto>>.Success(provider.GetAll()));
}

public sealed class GetReportExecutionQueryHandler(IReportExecutionRepository executions)
    : IRequestHandler<GetReportExecutionQuery, Result<ReportExecutionDto>>
{
    public async Task<Result<ReportExecutionDto>> Handle(GetReportExecutionQuery q, CancellationToken ct)
    {
        var execution = await executions.GetByIdAsync(q.ExecutionId, q.ClientId);
        if (execution is null)
            return Result<ReportExecutionDto>.Failure(Error.NotFound($"Report {q.ExecutionId} not found"));

        return Result<ReportExecutionDto>.Success(ListReportsQueryHandler.MapExecution(execution));
    }
}

// new — templates list
public sealed class ListReportTemplatesQueryHandler(IReportTemplateRepository repo)
    : IRequestHandler<ListReportTemplatesQuery, Result<IReadOnlyList<ReportTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<ReportTemplateDto>>> Handle(ListReportTemplatesQuery q, CancellationToken ct)
    {
        // DatasetType invalido e ignorado em vez de vazar um enum indefinido.
        var datasetType = q.DatasetType.HasValue && Enum.IsDefined(typeof(ReportDatasetType), q.DatasetType.Value)
            ? (ReportDatasetType)q.DatasetType.Value
            : (ReportDatasetType?)null;

        var templates = await repo.GetAllAsync(q.ClientId, datasetType, q.IsActive);
        var items = templates.Select(Map).ToList().AsReadOnly();
        return Result<IReadOnlyList<ReportTemplateDto>>.Success(items);
    }

    private static ReportTemplateDto Map(ReportTemplate t) => new(
        t.Id, t.ClientId, t.Name, t.Description, t.Instructions,
        (int)t.DatasetType, (int)t.DefaultFormat, t.IsActive, t.IsBuiltIn,
        t.Version, t.CreatedAt, t.UpdatedAt,
        t.LayoutJson, t.FiltersJson, t.ExecutionSchemaJson, t.CreatedBy, t.UpdatedBy);
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
            t.Version, t.CreatedAt, t.UpdatedAt,
            t.LayoutJson, t.FiltersJson, t.ExecutionSchemaJson, t.CreatedBy, t.UpdatedBy));
    }
}

// new — create template
public sealed class CreateReportTemplateCommandHandler(IReportTemplateRepository repo)
    : IRequestHandler<CreateReportTemplateCommand, Result<ReportTemplateDto>>
{
    public async Task<Result<ReportTemplateDto>> Handle(CreateReportTemplateCommand cmd, CancellationToken ct)
    {
        if (!Enum.IsDefined(typeof(ReportDatasetType), cmd.DatasetType))
            return Result<ReportTemplateDto>.Failure(Error.Validation(
                "datasetType",
                $"DatasetType {cmd.DatasetType} is not supported."));

        if (!Enum.IsDefined(typeof(ReportFormat), cmd.DefaultFormat))
            return Result<ReportTemplateDto>.Failure(Error.Validation(
                "defaultFormat",
                $"DefaultFormat {cmd.DefaultFormat} is not supported. Supported formats: {string.Join(", ", Enum.GetNames<ReportFormat>())}."));

        var createLayoutErrors = ReportLayoutValidator.ValidateJson(cmd.LayoutJson ?? "{}");
        if (createLayoutErrors.Count > 0)
            return Result<ReportTemplateDto>.Failure(Error.Validation("layoutJson", string.Join(" ", createLayoutErrors)));

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
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = cmd.CreatedBy,
            UpdatedBy = cmd.CreatedBy
        };

        var created = await repo.CreateAsync(template);
        return Result<ReportTemplateDto>.Success(new ReportTemplateDto(
            created.Id, created.ClientId, created.Name, created.Description,
            created.Instructions, (int)created.DatasetType, (int)created.DefaultFormat,
            created.IsActive, created.IsBuiltIn, created.Version,
            created.CreatedAt, created.UpdatedAt,
            created.LayoutJson, created.FiltersJson, created.ExecutionSchemaJson, created.CreatedBy, created.UpdatedBy));
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

        if (t.IsBuiltIn)
            return Result<ReportTemplateDto>.Failure(Error.Validation(
                "isBuiltIn",
                "Templates embutidos nao podem ser editados. Instale uma copia pela biblioteca."));

        if (cmd.LayoutJson is not null)
        {
            var layoutErrors = ReportLayoutValidator.ValidateJson(cmd.LayoutJson);
            if (layoutErrors.Count > 0)
                return Result<ReportTemplateDto>.Failure(Error.Validation("layoutJson", string.Join(" ", layoutErrors)));
        }

        if (cmd.DatasetType.HasValue && !Enum.IsDefined(typeof(ReportDatasetType), cmd.DatasetType.Value))
            return Result<ReportTemplateDto>.Failure(Error.Validation(
                "datasetType",
                $"DatasetType {cmd.DatasetType.Value} is not supported."));

        if (cmd.DefaultFormat.HasValue && !Enum.IsDefined(typeof(ReportFormat), cmd.DefaultFormat.Value))
            return Result<ReportTemplateDto>.Failure(Error.Validation(
                "defaultFormat",
                $"DefaultFormat {cmd.DefaultFormat.Value} is not supported. Supported formats: {string.Join(", ", Enum.GetNames<ReportFormat>())}."));

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
        t.UpdatedBy = cmd.UpdatedBy;
        // A versao e incrementada pelo repositorio (current.Version += 1).
        // Incrementar aqui tambem fazia a versao pular de 2 em 2.

        await repo.UpdateAsync(t);
        return Result<ReportTemplateDto>.Success(new ReportTemplateDto(
            t.Id, t.ClientId, t.Name, t.Description, t.Instructions,
            (int)t.DatasetType, (int)t.DefaultFormat, t.IsActive, t.IsBuiltIn,
            t.Version, t.CreatedAt, t.UpdatedAt,
            t.LayoutJson, t.FiltersJson, t.ExecutionSchemaJson, t.CreatedBy, t.UpdatedBy));
    }
}

// new — delete template
public sealed class DeleteReportTemplateCommandHandler(IReportTemplateRepository repo)
    : IRequestHandler<DeleteReportTemplateCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteReportTemplateCommand cmd, CancellationToken ct)
    {
        var template = await repo.GetByIdAsync(cmd.Id, cmd.ClientId);
        if (template is null)
            return Result<VoidResult>.Failure(Error.NotFound($"ReportTemplate {cmd.Id} not found"));

        if (template.IsBuiltIn)
            return Result<VoidResult>.Failure(Error.Validation(
                "isBuiltIn",
                "Templates embutidos nao podem ser excluidos. Instale uma copia pela biblioteca."));

        var deleted = await repo.DeleteAsync(cmd.Id, cmd.ClientId);
        if (!deleted)
            return Result<VoidResult>.Failure(Error.NotFound($"ReportTemplate {cmd.Id} not found"));
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

// new — run report now
public sealed class RunReportNowCommandHandler(IReportService reportService, IReportTemplateRepository templateRepo, IReportExecutionRepository execRepo)
    : IRequestHandler<RunReportNowCommand, Result<RunReportResultDto>>
{
    /// <summary>
    /// Resolve o formato pedido. O valor 1 era o extinto Pdf: em bases que ainda
    /// nao rodaram a migracao M194 ele nao pode bloquear a geracao — cai para o
    /// formato do template (ou Markdown). Demais valores invalidos seguem 400.
    /// </summary>
    private static ReportFormat? ResolveRequestedFormat(int requested, ReportFormat templateDefault)
    {
        if (Enum.IsDefined(typeof(ReportFormat), requested))
            return (ReportFormat)requested;

        if (requested == 1)
            return Enum.IsDefined(typeof(ReportFormat), templateDefault) ? templateDefault : ReportFormat.Markdown;

        return null;
    }

    public async Task<Result<RunReportResultDto>> Handle(RunReportNowCommand cmd, CancellationToken ct)
    {
        var template = await templateRepo.GetByIdAsync(cmd.TemplateId, cmd.ClientId);
        if (template is null)
            return Result<RunReportResultDto>.Failure(Error.NotFound($"ReportTemplate {cmd.TemplateId} not found"));

        var resolvedFormat = ResolveRequestedFormat(cmd.Format, template.DefaultFormat);
        if (resolvedFormat is null)
            return Result<RunReportResultDto>.Failure(Error.Validation(
                "format",
                $"Format {cmd.Format} is not supported. Supported formats: {string.Join(", ", Enum.GetNames<ReportFormat>())}."));

        var execution = new ReportExecution
        {
            Id = Guid.NewGuid(),
            TemplateId = cmd.TemplateId,
            ClientId = cmd.ClientId,
            Format = resolvedFormat.Value,
            FiltersJson = cmd.FiltersJson,
            Status = ReportExecutionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ScheduleId = cmd.ScheduleId,
            CreatedBy = cmd.CreatedBy
        };

        execution = await execRepo.CreateAsync(execution);
        try
        {
            execution = await reportService.ProcessExecutionAsync(
                execution.Id,
                cmd.ClientId,
                ct,
                new ReportQueryScope(cmd.ClientId, cmd.SiteId));
        }
        catch (OperationCanceledException)
        {
            return Result<RunReportResultDto>.Failure(Error.Validation(
                "report",
                "A geracao do relatorio excedeu o tempo limite. Consulte a execucao para detalhes."));
        }
        catch (Exception ex)
        {
            // A execucao ja foi marcada como Failed e a notificacao publicada em
            // ProcessExecutionAsync. Devolver a causa real evita um 500 opaco.
            return Result<RunReportResultDto>.Failure(Error.Validation("report", ex.Message));
        }

        var downloadPath = cmd.ClientId.HasValue && cmd.ClientId.Value != Guid.Empty
            ? $"/api/v1/reports/executions/{execution.Id}/download?clientId={cmd.ClientId}"
            : $"/api/v1/reports/executions/{execution.Id}/download";

        return Result<RunReportResultDto>.Success(new RunReportResultDto(
            ExecutionId: execution.Id,
            Status: (int)execution.Status,
            Format: (int)execution.Format,
            RowCount: execution.RowCount,
            ResultSizeBytes: execution.StorageSizeBytes,
            ContentType: execution.StorageContentType,
            DownloadPath: downloadPath,
            TemplateName: template.Name,
            CreatedAt: execution.CreatedAt,
            FinishedAt: execution.FinishedAt));
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

        // O preview devolve HTML para o navegador: o layout precisa ser validado
        // aqui tambem (nao apenas na gravacao), pois o layout inline vem do corpo.
        var layoutErrors = ReportLayoutValidator.ValidateJson(template.LayoutJson);
        if (layoutErrors.Count > 0)
            return Result<ReportPreviewResultDto>.Failure(Error.Validation("layoutJson", string.Join(" ", layoutErrors)));

        var isHtml = string.Equals(cmd.PreviewMode, "html", StringComparison.OrdinalIgnoreCase);

        if (isHtml)
        {
            var htmlResult = await reportService.PreviewHtmlAsync(
                template, cmd.FiltersJson, ct, new ReportQueryScope(cmd.ClientId, cmd.SiteId));
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

        var format = ResolveFormat(cmd.Format)
            ?? (Enum.IsDefined(typeof(ReportFormat), template.DefaultFormat) ? template.DefaultFormat : ReportFormat.Markdown);
        var result = await reportService.PreviewAsync(
            template, format, cmd.FiltersJson, ct, new ReportQueryScope(cmd.ClientId, cmd.SiteId));
        var contentType = format switch
        {
            ReportFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ReportFormat.Csv => "text/csv; charset=utf-8",
            ReportFormat.Markdown => "text/markdown; charset=utf-8",
            _ => "application/octet-stream"
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
            DefaultFormat = ReportFormat.Markdown,
            LayoutJson = input.LayoutJson ?? "{}",
            FiltersJson = input.FiltersJson,
            IsActive = true,
            IsBuiltIn = false,
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static ReportDatasetType? ResolveDatasetType(object? datasetType, string? datasetKey)
    {
        var raw = datasetType?.ToString() ?? datasetKey;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Número (ex: "4"). Se for numérico e não existir no enum, NÃO tenta
        // Enum.TryParse: ele aceita "999" e devolve um valor indefinido.
        if (int.TryParse(raw, out var numeric))
            return Enum.IsDefined(typeof(ReportDatasetType), numeric) ? (ReportDatasetType)numeric : null;

        // Nome do enum (ex: "AgentHardware")
        if (Enum.TryParse<ReportDatasetType>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(typeof(ReportDatasetType), parsed))
            return parsed;

        // camelCase (ex: "agentHardware") → PascalCase
        var pascal = char.ToUpperInvariant(raw[0]) + raw[1..];
        if (Enum.TryParse<ReportDatasetType>(pascal, ignoreCase: true, out var parsedPascal))
            return parsedPascal;

        return null;
    }

    // Resolve o formato de relatório a partir de um valor flexível (int, nome do
    // enum ou camelCase), seguindo o mesmo padrão do ResolveDatasetType.
    private static ReportFormat? ResolveFormat(object? format)
    {
        var raw = format?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Número (ex: "0" = Xlsx, "2" = Csv, "3" = Markdown). Numérico fora do
        // enum não deve cair no Enum.TryParse (que aceitaria "999").
        if (int.TryParse(raw, out var numeric))
            return Enum.IsDefined(typeof(ReportFormat), numeric) ? (ReportFormat)numeric : null;

        // Nome do enum (ex: "Markdown")
        if (Enum.TryParse<ReportFormat>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(typeof(ReportFormat), parsed))
            return parsed;

        // camelCase (ex: "markdown") → PascalCase
        var pascal = char.ToUpperInvariant(raw[0]) + raw[1..];
        if (Enum.TryParse<ReportFormat>(pascal, ignoreCase: true, out var parsedPascal))
            return parsedPascal;

        return null;
    }
}

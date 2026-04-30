using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using Discovery.Api.Filters;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/reports")]
public class ReportsController : ControllerBase
{
    private static readonly IReadOnlyDictionary<ReportDatasetType, ReportDatasetSpecification> DatasetCatalog = BuildDatasetCatalog();

    private readonly IReportTemplateRepository _templateRepository;
    private readonly IReportExecutionRepository _executionRepository;
    private readonly IReportScheduleRepository _scheduleRepository;
    private readonly IReportService _reportService;
    private readonly ReportFormat[] _enabledFormats;

    public ReportsController(
        IReportTemplateRepository templateRepository,
        IReportExecutionRepository executionRepository,
        IReportScheduleRepository scheduleRepository,
        IReportService reportService,
        IOptions<ReportingOptions> reportingOptions)
    {
        _templateRepository = templateRepository;
        _executionRepository = executionRepository;
        _scheduleRepository = scheduleRepository;
        _reportService = reportService;

        var options = reportingOptions.Value;
        _enabledFormats = options.EnablePdf
            ? [ReportFormat.Xlsx, ReportFormat.Csv, ReportFormat.Pdf, ReportFormat.Markdown]
            : [ReportFormat.Xlsx, ReportFormat.Csv, ReportFormat.Markdown];
    }

    [HttpGet("datasets")]
    [RequirePermission(ResourceType.Reports, ActionType.View)]
    public IActionResult GetDatasetCatalog()
    {
        var datasets = DatasetCatalog
            .Select(item => new
            {
                type = item.Key,
                key = ToCamelCase(item.Key.ToString()),
                name = GetDatasetDisplayName(item.Key),
                description = GetDatasetDescription(item.Key),
                fields = item.Value.Fields,
                fieldMetadata = item.Value.Fields.Select(field => new
                {
                    name = field,
                    dataType = InferFieldType(field),
                    isJoinKey = IsJoinKeyField(field)
                }),
                joinCapabilities = new
                {
                    supportsJoin = true,
                    allowedJoinTypes = new[] { "left", "inner" },
                    preferredKeys = item.Value.Fields.Where(IsJoinKeyField).ToArray(),
                    defaultJoinType = "left"
                },
                formats = _enabledFormats,
                executionSchema = item.Value.ExecutionSchema,
                supportsAsPrimarySource = true,
                supportsAsSecondarySource = true
            });

        return Ok(datasets);
    }

    [HttpGet("layout-schema")]
    [RequirePermission(ResourceType.Reports, ActionType.View)]
    public IActionResult GetLayoutSchema()
    {
        return Ok(new
        {
            previewModes = new[] { "document", "html" },
            responseDispositions = new[] { "inline", "attachment" },
            orientations = ReportLayoutValidator.GetSupportedOrientations(),
            columnFormats = ReportLayoutValidator.GetSupportedColumnFormats(),
            summaryAggregates = ReportLayoutValidator.GetSupportedSummaryAggregates(),
            limits = ReportLayoutValidator.GetLimits(),
            multiSource = new
            {
                enabled = true,
                aliasPattern = "^[A-Za-z_][A-Za-z0-9_]*$",
                allowedJoinTypes = new[] { "left", "inner" },
                fieldReferenceMode = "alias.field",
                dataSourceContract = new
                {
                    requiredFields = new[] { "datasetType", "alias" },
                    joinRequiredFromIndex = 1,
                    joinFields = new[] { "joinToAlias", "sourceKey", "targetKey", "joinType" }
                },
                examples = new[]
                {
                    "hw.agentHostname",
                    "sw.softwareName",
                    "labels.automaticLabels"
                }
            },
            notes = new[]
            {
                "Use columns for a single main table.",
                "Use sections for multiple subtables in the same report or group.",
                "Use groupDetails to render key-value cards above each grouped section.",
                "Use groupBy with groupTitleTemplate to create sections such as Agent X followed by its data.",
                "For multi-source reports, use layout.dataSources and reference fields as alias.field."
            }
        });
    }

    [HttpGet("autocomplete")]
    [RequirePermission(ResourceType.Reports, ActionType.View)]
    public IActionResult GetAutocomplete(
        [FromQuery] string? term,
        [FromQuery] ReportDatasetType? datasetType,
        [FromQuery] string? alias)
    {
        var normalizedTerm = string.IsNullOrWhiteSpace(term)
            ? null
            : term.Trim();

        var datasets = DatasetCatalog
            .Where(item => !datasetType.HasValue || item.Key == datasetType.Value)
            .Select(item =>
            {
                var defaultAlias = string.IsNullOrWhiteSpace(alias)
                    ? GetDefaultAlias(item.Key)
                    : alias.Trim();

                var fields = item.Value.Fields
                    .Select(field => new
                    {
                        datasetType = item.Key,
                        datasetKey = ToCamelCase(item.Key.ToString()),
                        datasetName = GetDatasetDisplayName(item.Key),
                        field,
                        reference = $"{defaultAlias}.{field}",
                        dataType = InferFieldType(field),
                        isJoinKey = IsJoinKeyField(field),
                        defaultAlias
                    });

                if (normalizedTerm is null)
                    return fields;

                return fields.Where(entry =>
                    entry.field.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase)
                    || entry.reference.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase)
                    || entry.datasetName.Contains(normalizedTerm, StringComparison.OrdinalIgnoreCase));
            })
            .SelectMany(item => item)
            .OrderBy(item => item.datasetName)
            .ThenBy(item => item.field)
            .Take(500)
            .ToArray();

        return Ok(new
        {
            fieldReferenceMode = "alias.field",
            total = datasets.Length,
            items = datasets
        });
    }

    [HttpPost("templates")]
    [RequirePermission(ResourceType.Reports, ActionType.Create)]
    public async Task<IActionResult> CreateTemplate([FromBody] CreateReportTemplateRequest request)
    {
        var template = new ReportTemplate
        {
            ClientId = null,
            Name = request.Name,
            Description = request.Description,
            Instructions = request.Instructions,
            ExecutionSchemaJson = request.ExecutionSchemaJson,
            DatasetType = request.DatasetType,
            DefaultFormat = request.DefaultFormat,
            LayoutJson = request.LayoutJson,
            FiltersJson = request.FiltersJson,
            IsActive = true,
            CreatedBy = request.CreatedBy,
            UpdatedBy = request.CreatedBy
        };

        var created = await _templateRepository.CreateAsync(template);
        return CreatedAtAction(nameof(GetTemplateById), new { id = created.Id }, ToTemplateResponse(created));
    }

    [HttpGet("templates/library")]
    [RequirePermission(ResourceType.Reports, ActionType.View)]
    public async Task<IActionResult> GetLibraryTemplates(
        [FromQuery] ReportDatasetType? datasetType)
    {
        var templates = await _templateRepository.GetAllAsync(null, datasetType, isActive: true);
        var builtIn = templates.Where(t => t.IsBuiltIn).Select(ToTemplateResponse);
        return Ok(builtIn);
    }

    [HttpPost("templates/library/{id:guid}/install")]
    public async Task<IActionResult> InstallLibraryTemplate(Guid id, [FromQuery] string? createdBy)
    {
        var source = await _templateRepository.GetByIdAsync(id, null);
        if (source is null || !source.IsBuiltIn)
            return NotFound(new { error = "Built-in template not found." });

        var clone = new ReportTemplate
        {
            ClientId = null,
            Name = source.Name,
            Description = source.Description,
            Instructions = source.Instructions,
            ExecutionSchemaJson = source.ExecutionSchemaJson,
            DatasetType = source.DatasetType,
            DefaultFormat = source.DefaultFormat,
            LayoutJson = source.LayoutJson,
            FiltersJson = source.FiltersJson,
            IsActive = true,
            IsBuiltIn = false,
            CreatedBy = createdBy ?? "user",
            UpdatedBy = createdBy ?? "user"
        };

        var created = await _templateRepository.CreateAsync(clone);
        return CreatedAtAction(nameof(GetTemplateById), new { id = created.Id }, ToTemplateResponse(created));
    }

    [HttpGet("templates")]
    public async Task<IActionResult> GetTemplates(
        [FromQuery] ReportDatasetType? datasetType,
        [FromQuery] bool? isActive = true)
    {
        var templates = await _templateRepository.GetAllAsync(null, datasetType, isActive);
        return Ok(templates.Select(ToTemplateResponse));
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<IActionResult> GetTemplateById(Guid id)
    {
        var template = await _templateRepository.GetByIdAsync(id, null);
        return template is null ? NotFound() : Ok(ToTemplateResponse(template));
    }

    [HttpGet("templates/{id:guid}/history")]
    public async Task<IActionResult> GetTemplateHistory(Guid id, [FromQuery] int limit = 50)
    {
        var template = await _templateRepository.GetByIdAsync(id, null);
        if (template is null)
            return NotFound();

        var history = await _templateRepository.GetHistoryAsync(id, limit);
        return Ok(history);
    }

    [HttpPut("templates/{id:guid}")]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] UpdateReportTemplateRequest request)
    {
        var current = await _templateRepository.GetByIdAsync(id, null);
        if (current is null)
            return NotFound();

        if (request.Name is not null)
            current.Name = request.Name;
        if (request.Description is not null)
            current.Description = request.Description;
        if (request.Instructions is not null)
            current.Instructions = request.Instructions;
        if (request.ExecutionSchemaJson is not null)
            current.ExecutionSchemaJson = request.ExecutionSchemaJson;
        if (request.DatasetType.HasValue)
            current.DatasetType = request.DatasetType.Value;
        if (request.DefaultFormat.HasValue)
            current.DefaultFormat = request.DefaultFormat.Value;
        if (request.LayoutJson is not null)
            current.LayoutJson = request.LayoutJson;
        if (request.FiltersJson is not null)
            current.FiltersJson = request.FiltersJson;
        if (request.IsActive.HasValue)
            current.IsActive = request.IsActive.Value;
        current.UpdatedBy = request.UpdatedBy;

        await _templateRepository.UpdateAsync(current);
        return Ok(ToTemplateResponse(current));
    }

    [HttpDelete("templates/{id:guid}")]
    public async Task<IActionResult> DeleteTemplate(Guid id)
    {
        var deleted = await _templateRepository.DeleteAsync(id, null);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("run")]
    public async Task<IActionResult> RunReport([FromBody] RunReportRequest request)
    {
        var template = await _templateRepository.GetByIdAsync(request.TemplateId, null);
        if (template is null)
            return NotFound(new { error = "Template not found." });

        var effectiveFiltersJson = MergeFiltersJson(template.FiltersJson, request.FiltersJson);

        var selectedFormat = request.Format ?? template.DefaultFormat;
        if (!_enabledFormats.Contains(selectedFormat))
        {
            return BadRequest(new
            {
                error = "Format not enabled in current reporting configuration.",
                requested = selectedFormat,
                enabled = _enabledFormats
            });
        }

        var execution = new ReportExecution
        {
            TemplateId = template.Id,
            ClientId = TryGetGuidFromFilters(effectiveFiltersJson, "clientId"),
            Format = selectedFormat,
            FiltersJson = effectiveFiltersJson,
            Status = ReportExecutionStatus.Pending,
            CreatedBy = request.CreatedBy
        };

        // Usar schema customizado do template, se disponível, senão schema padrão do dataset
        ReportExecutionSchema? executionSchema = null;
        if (!string.IsNullOrWhiteSpace(template.ExecutionSchemaJson))
        {
            try
            {
                executionSchema = JsonSerializer.Deserialize<ReportExecutionSchema>(template.ExecutionSchemaJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                // Schema customizado inválido, usa padrão do dataset
            }
        }

        var validationErrors = ValidateTemplateFilters(template.DatasetType, execution.FiltersJson, executionSchema);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new
            {
                error = "Invalid report filters for selected template.",
                datasetType = template.DatasetType,
                details = validationErrors
            });
        }

        var created = await _executionRepository.CreateAsync(execution);

        if (request.RunAsync)
        {
            return Accepted(new
            {
                executionId = created.Id,
                status = created.Status,
                message = "Report execution queued for async processing."
            });
        }

        var processed = await _reportService.ProcessExecutionAsync(created.Id, created.ClientId);

        var downloadPath = processed.ClientId.HasValue
            ? $"/api/reports/executions/{processed.Id}/download?clientId={processed.ClientId}"
            : $"/api/reports/executions/{processed.Id}/download";

        return Ok(new
        {
            executionId = processed.Id,
            status = processed.Status,
            rowCount = processed.RowCount,
            contentType = processed.StorageContentType,
            resultSizeBytes = processed.StorageSizeBytes,
            downloadPath
        });
    }

    [HttpPost("preview")]
    public async Task<IActionResult> PreviewReport([FromBody] PreviewReportRequest request, CancellationToken cancellationToken)
    {
        if (request.TemplateId is null && request.Template is null)
        {
            return BadRequest(new
            {
                error = "TemplateId or template draft must be provided for preview."
            });
        }

        ReportTemplate? baseTemplate = null;
        if (request.TemplateId.HasValue)
        {
            baseTemplate = await _templateRepository.GetByIdAsync(request.TemplateId.Value, null);
            if (baseTemplate is null)
                return NotFound(new { error = "Template not found." });
        }

        var (effectiveTemplate, previewErrors) = BuildPreviewTemplate(baseTemplate, request.Template);
        if (previewErrors.Count > 0)
        {
            return BadRequest(new
            {
                error = "Invalid preview template.",
                details = previewErrors
            });
        }

        var layoutErrors = ReportLayoutValidator.ValidateJson(effectiveTemplate!.LayoutJson);
        if (layoutErrors.Count > 0)
        {
            return BadRequest(new
            {
                error = "Invalid report layout for preview.",
                details = layoutErrors
            });
        }

        effectiveTemplate.FiltersJson = MergeFiltersJson(effectiveTemplate.FiltersJson, request.FiltersJson);

        var previewMode = string.Equals(request.PreviewMode, "html", StringComparison.OrdinalIgnoreCase)
            ? "html"
            : "document";

        var selectedFormat = request.Format ?? effectiveTemplate.DefaultFormat;
        if (previewMode != "html" && !_enabledFormats.Contains(selectedFormat))
        {
            return BadRequest(new
            {
                error = "Format not enabled in current reporting configuration.",
                requested = selectedFormat,
                enabled = _enabledFormats
            });
        }

        var validationErrors = ValidateTemplateFilters(
            effectiveTemplate.DatasetType,
            effectiveTemplate.FiltersJson,
            DeserializeExecutionSchema(effectiveTemplate.ExecutionSchemaJson, effectiveTemplate.DatasetType));

        if (validationErrors.Count > 0)
        {
            return BadRequest(new
            {
                error = "Invalid report filters for preview.",
                datasetType = effectiveTemplate.DatasetType,
                details = validationErrors
            });
        }

        if (previewMode == "html")
        {
            var htmlPreview = await _reportService.PreviewHtmlAsync(effectiveTemplate, effectiveTemplate.FiltersJson, cancellationToken);
            Response.Headers.Append("X-Report-Preview", "true");
            Response.Headers.Append("X-Report-RowCount", htmlPreview.RowCount.ToString());
            Response.Headers.Append("X-Report-Title", htmlPreview.Title);
            Response.Headers.Append("X-Report-Format", "Html");

            return Content(htmlPreview.Html, "text/html; charset=utf-8");
        }

        var preview = await _reportService.PreviewAsync(effectiveTemplate, selectedFormat, effectiveTemplate.FiltersJson, cancellationToken);
        Response.Headers.Append("X-Report-Preview", "true");
        Response.Headers.Append("X-Report-RowCount", preview.RowCount.ToString());
        Response.Headers.Append("X-Report-Title", preview.Title);
        Response.Headers.Append("X-Report-Format", selectedFormat.ToString());

        var downloadName = BuildPreviewFileName(request.FileName, preview.Title, preview.Document.FileExtension);
        var disposition = string.Equals(request.ResponseDisposition, "attachment", StringComparison.OrdinalIgnoreCase)
            ? "attachment"
            : "inline";
        Response.Headers.ContentDisposition = $"{disposition}; filename=\"{downloadName}\"";
        return File(preview.Document.Content, preview.Document.ContentType);
    }

    [HttpGet("executions/{id:guid}")]
    public async Task<IActionResult> GetExecutionById(Guid id, [FromQuery] Guid? clientId)
    {
        var execution = await _executionRepository.GetByIdAsync(id, clientId);
        return execution is null ? NotFound() : Ok(execution);
    }

    [HttpGet("executions")]
    public async Task<IActionResult> GetExecutions([FromQuery] Guid? clientId, [FromQuery] int limit = 50)
    {
        var executions = await _executionRepository.GetRecentByClientAsync(clientId, limit);
        return Ok(executions);
    }

    [HttpGet("executions/{id:guid}/download")]
    public async Task<IActionResult> DownloadExecution(Guid id, [FromQuery] Guid? clientId)
    {
        var url = await _reportService.GetPresignedDownloadUrlAsync(id, clientId);
        if (string.IsNullOrWhiteSpace(url))
            return NotFound(new { error = "Report file not available." });

        return Redirect(url);
    }

    /// <summary>
    /// <summary>
    /// [DESCONTINUADO] Compat endpoint: use <c>GET executions/{id}/download</c>.
    /// Redireciona para URL pré-assinada. Será removido em uma versão futura.
    /// </summary>
    [HttpGet("executions/{id:guid}/download-stream")]
    [Obsolete("Use GET executions/{id}/download instead. This endpoint will be removed in a future version.")]
    public async Task<IActionResult> DownloadExecutionStream(Guid id, [FromQuery] Guid? clientId)
    {
        Response.Headers.Append("Deprecation", "true");
        Response.Headers.Append("Link", $"</api/reports/executions/{id}/download>; rel=\"successor-version\"");

        var url = await _reportService.GetPresignedDownloadUrlAsync(id, clientId);
        if (string.IsNullOrWhiteSpace(url))
            return NotFound(new { error = "Report file not available." });

        return Redirect(url);
    }

    // ───────────────────── Schedule Endpoints ─────────────────────

    [HttpPost("schedules")]
    public async Task<IActionResult> CreateSchedule([FromBody] CreateReportScheduleRequest request)
    {
        var template = await _templateRepository.GetByIdAsync(request.TemplateId, null);
        if (template is null)
            return NotFound(new { error = "Template not found." });

        if (!_enabledFormats.Contains(request.Format))
        {
            return BadRequest(new
            {
                error = "Format not enabled in current reporting configuration.",
                requested = request.Format,
                enabled = _enabledFormats
            });
        }

        var schedule = new ReportSchedule
        {
            TemplateId = request.TemplateId,
            ClientId = TryGetGuidFromFilters(request.FiltersJson, "clientId"),
            Format = request.Format,
            FiltersJson = request.FiltersJson,
            ScheduleLabel = request.Label,
            CronExpression = request.CronExpression ?? "0 8 * * 1",
            TimeZoneId = request.TimeZoneId ?? "UTC",
            MaxRetainedExecutions = Math.Clamp(request.MaxRetainedExecutions ?? 10, 1, 100),
            IsActive = request.IsActive ?? true,
            CreatedBy = request.CreatedBy,
            UpdatedBy = request.CreatedBy
        };

        var created = await _scheduleRepository.CreateAsync(schedule);
        return CreatedAtAction(nameof(GetScheduleById), new { id = created.Id }, created);
    }

    [HttpGet("schedules")]
    public async Task<IActionResult> GetSchedules(
        [FromQuery] Guid? templateId,
        [FromQuery] Guid? clientId,
        [FromQuery] bool? isActive)
    {
        IReadOnlyList<ReportSchedule> schedules;
        if (templateId.HasValue)
            schedules = await _scheduleRepository.GetByTemplateAsync(templateId.Value, clientId);
        else
            schedules = await _scheduleRepository.GetAllAsync(clientId, isActive ?? true);

        return Ok(schedules);
    }

    [HttpGet("schedules/{id:guid}")]
    public async Task<IActionResult> GetScheduleById(Guid id, [FromQuery] Guid? clientId)
    {
        var schedule = await _scheduleRepository.GetByIdAsync(id, clientId);
        return schedule is null ? NotFound() : Ok(schedule);
    }

    [HttpPut("schedules/{id:guid}")]
    public async Task<IActionResult> UpdateSchedule(Guid id, [FromBody] UpdateReportScheduleRequest request)
    {
        var schedule = await _scheduleRepository.GetByIdAsync(id, request.ClientId);
        if (schedule is null)
            return NotFound();

        if (request.Label is not null)
            schedule.ScheduleLabel = request.Label;
        if (request.CronExpression is not null)
            schedule.CronExpression = request.CronExpression;
        if (request.TimeZoneId is not null)
            schedule.TimeZoneId = request.TimeZoneId;
        if (request.MaxRetainedExecutions.HasValue)
            schedule.MaxRetainedExecutions = Math.Clamp(request.MaxRetainedExecutions.Value, 1, 100);
        if (request.IsActive.HasValue)
            schedule.IsActive = request.IsActive.Value;
        if (request.FiltersJson is not null)
            schedule.FiltersJson = request.FiltersJson;
        schedule.UpdatedBy = request.UpdatedBy;

        await _scheduleRepository.UpdateAsync(schedule);
        return Ok(schedule);
    }

    [HttpDelete("schedules/{id:guid}")]
    public async Task<IActionResult> DeleteSchedule(Guid id, [FromQuery] Guid? clientId)
    {
        var deleted = await _scheduleRepository.DeleteAsync(id, clientId);
        return deleted ? NoContent() : NotFound();
    }

    // ───────────────────── Private helpers ─────────────────────

    private static Guid? TryGetGuidFromFilters(string? filtersJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(filtersJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(filtersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!doc.RootElement.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                return null;

            var value = property.GetString();
            return Guid.TryParse(value, out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? MergeFiltersJson(string? templateFiltersJson, string? runtimeFiltersJson)
    {
        if (string.IsNullOrWhiteSpace(templateFiltersJson))
            return runtimeFiltersJson;

        if (string.IsNullOrWhiteSpace(runtimeFiltersJson))
            return templateFiltersJson;

        try
        {
            var templateNode = JsonNode.Parse(templateFiltersJson) as JsonObject;
            var runtimeNode = JsonNode.Parse(runtimeFiltersJson) as JsonObject;

            if (templateNode is null)
                return runtimeFiltersJson;

            if (runtimeNode is null)
                return templateFiltersJson;

            foreach (var (key, value) in runtimeNode)
            {
                templateNode[key] = value?.DeepClone();
            }

            return templateNode.ToJsonString();
        }
        catch
        {
            // Se houver qualquer problema de parse/merge, preserva comportamento anterior (runtime sobrescreve template).
            return runtimeFiltersJson;
        }
    }

    private static ReportTemplateResponse ToTemplateResponse(ReportTemplate template)
    {
        var executionSchema = DeserializeExecutionSchema(template.ExecutionSchemaJson, template.DatasetType);

        return new ReportTemplateResponse(
            template.Id,
            template.ClientId,
            template.Name,
            template.Description,
            template.Instructions,
            template.DatasetType,
            template.DefaultFormat,
            template.LayoutJson,
            template.FiltersJson,
            template.IsActive,
            template.Version,
            template.CreatedAt,
            template.UpdatedAt,
            template.CreatedBy,
            template.UpdatedBy,
            executionSchema,
            template.ExecutionSchemaJson);
    }

    private static ReportExecutionSchema? DeserializeExecutionSchema(string? executionSchemaJson, ReportDatasetType datasetType)
    {
        if (!string.IsNullOrWhiteSpace(executionSchemaJson))
        {
            try
            {
                return JsonSerializer.Deserialize<ReportExecutionSchema>(executionSchemaJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                // ignora e usa schema padrão do dataset
            }
        }

        return DatasetCatalog.TryGetValue(datasetType, out var specification)
            ? specification.ExecutionSchema
            : null;
    }

    private static (ReportTemplate? Template, List<string> Errors) BuildPreviewTemplate(ReportTemplate? baseTemplate, PreviewReportTemplateDraft? draft)
    {
        var errors = new List<string>();

        if (baseTemplate is null && draft is null)
        {
            errors.Add("A preview template or a persisted templateId is required.");
            return (null, errors);
        }

        var template = baseTemplate is null
            ? new ReportTemplate
            {
                Name = "Preview Report",
                LayoutJson = "{}",
                DefaultFormat = ReportFormat.Xlsx
            }
            : new ReportTemplate
            {
                Id = baseTemplate.Id,
                ClientId = baseTemplate.ClientId,
                Name = baseTemplate.Name,
                Description = baseTemplate.Description,
                Instructions = baseTemplate.Instructions,
                ExecutionSchemaJson = baseTemplate.ExecutionSchemaJson,
                DatasetType = baseTemplate.DatasetType,
                DefaultFormat = baseTemplate.DefaultFormat,
                LayoutJson = baseTemplate.LayoutJson,
                FiltersJson = baseTemplate.FiltersJson,
                IsActive = baseTemplate.IsActive,
                Version = baseTemplate.Version,
                CreatedAt = baseTemplate.CreatedAt,
                UpdatedAt = baseTemplate.UpdatedAt,
                CreatedBy = baseTemplate.CreatedBy,
                UpdatedBy = baseTemplate.UpdatedBy
            };

        if (draft is not null)
        {
            if (!string.IsNullOrWhiteSpace(draft.Name))
                template.Name = draft.Name;
            if (draft.Description is not null)
                template.Description = draft.Description;
            if (draft.Instructions is not null)
                template.Instructions = draft.Instructions;
            if (draft.ExecutionSchemaJson is not null)
                template.ExecutionSchemaJson = draft.ExecutionSchemaJson;
            if (draft.DatasetType.HasValue)
                template.DatasetType = draft.DatasetType.Value;
            if (draft.DefaultFormat.HasValue)
                template.DefaultFormat = draft.DefaultFormat.Value;
            if (draft.LayoutJson is not null)
                template.LayoutJson = draft.LayoutJson;
            if (draft.FiltersJson is not null)
                template.FiltersJson = draft.FiltersJson;
        }

        if (string.IsNullOrWhiteSpace(template.Name))
            template.Name = "Preview Report";

        if (string.IsNullOrWhiteSpace(template.LayoutJson))
            template.LayoutJson = "{}";

        if (template.DatasetType == default && baseTemplate is null && draft?.DatasetType is null)
            errors.Add("DatasetType is required when previewing a draft template.");

        return (template, errors);
    }

    private static string BuildPreviewFileName(string? requestedFileName, string title, string extension)
    {
        var candidate = string.IsNullOrWhiteSpace(requestedFileName) ? title : requestedFileName;
        candidate = string.IsNullOrWhiteSpace(candidate) ? "report-preview" : candidate.Trim();

        var sanitized = new string(candidate
            .Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch)
            .ToArray())
            .Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "report-preview";

        if (!sanitized.EndsWith($".{extension}", StringComparison.OrdinalIgnoreCase))
            sanitized = $"{sanitized}.{extension}";

        return sanitized;
    }

    private static List<string> ValidateTemplateFilters(ReportDatasetType datasetType, string? filtersJson, ReportExecutionSchema? customSchema = null)
    {
        // Prioriza schema customizado (por template) sobre schema padrão (por dataset)
        ReportExecutionSchema? schema = customSchema;
        
        if (schema is null)
        {
            if (!DatasetCatalog.TryGetValue(datasetType, out var specification))
                return [];
            
            schema = specification.ExecutionSchema;
        }

        if (string.IsNullOrWhiteSpace(filtersJson))
        {
            var requiredNames = schema.Filters
                .Where(filter => filter.Required)
                .Select(filter => filter.Name)
                .ToArray();

            return requiredNames.Length == 0
                ? []
                : [$"Required filters missing: {string.Join(", ", requiredNames)}."];
        }

        JsonElement filters;
        try
        {
            using var doc = JsonDocument.Parse(filtersJson);
            filters = doc.RootElement.Clone();
        }
        catch
        {
            return ["FiltersJson must be a valid JSON object."];
        }

        if (filters.ValueKind != JsonValueKind.Object)
            return ["FiltersJson must be a JSON object."];

        var errors = new List<string>();

        foreach (var filter in schema.Filters.Where(filter => filter.Required))
        {
            if (!HasValue(filters, filter.Name))
                errors.Add($"Filter '{filter.Name}' is required.");
        }

        foreach (var filter in schema.Filters)
        {
            if (!HasValue(filters, filter.Name))
                continue;

            if (filter.Type == ReportFilterFieldType.Enum &&
                filter.AllowedValues is { Length: > 0 } &&
                !IsAllowedEnumValue(filters, filter.Name, filter.AllowedValues))
            {
                errors.Add($"Filter '{filter.Name}' has invalid value. Allowed: {string.Join(", ", filter.AllowedValues)}.");
            }

            if (filter.Type is ReportFilterFieldType.Integer or ReportFilterFieldType.Decimal)
            {
                var numberValue = GetNumber(filters, filter.Name);
                if (numberValue.HasValue)
                {
                    if (filter.Min.HasValue && numberValue.Value < filter.Min.Value)
                        errors.Add($"Filter '{filter.Name}' must be greater than or equal to {filter.Min.Value}.");

                    if (filter.Max.HasValue && numberValue.Value > filter.Max.Value)
                        errors.Add($"Filter '{filter.Name}' must be less than or equal to {filter.Max.Value}.");
                }
            }

            if (filter.Type == ReportFilterFieldType.Text && filter.MaxLength.HasValue)
            {
                var textValue = GetString(filters, filter.Name);
                if (!string.IsNullOrEmpty(textValue) && textValue.Length > filter.MaxLength.Value)
                    errors.Add($"Filter '{filter.Name}' exceeds maximum length of {filter.MaxLength.Value} characters.");
            }

            if (filter.Type == ReportFilterFieldType.Guid)
            {
                var guidValue = GetString(filters, filter.Name);
                if (!string.IsNullOrEmpty(guidValue) && !Guid.TryParse(guidValue, out _))
                    errors.Add($"Filter '{filter.Name}' must be a valid GUID.");
            }

            if (filter.Type == ReportFilterFieldType.DateTime)
            {
                var dateValue = GetString(filters, filter.Name);
                if (!string.IsNullOrEmpty(dateValue) && !DateTime.TryParse(dateValue, out _))
                    errors.Add($"Filter '{filter.Name}' must be a valid DateTime (ISO 8601 format recommended).");
            }

            if (filter.Type == ReportFilterFieldType.Boolean)
            {
                if (filters.TryGetProperty(filter.Name, out var boolValue) && boolValue.ValueKind != JsonValueKind.True && boolValue.ValueKind != JsonValueKind.False)
                    errors.Add($"Filter '{filter.Name}' must be a boolean (true/false).");
            }
        }

        if (HasValue(filters, "orderBy"))
        {
            var orderBy = GetString(filters, "orderBy");
            if (!string.IsNullOrWhiteSpace(orderBy) && !schema.AllowedSortFields.Contains(orderBy, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"orderBy '{orderBy}' is not allowed. Allowed: {string.Join(", ", schema.AllowedSortFields)}.");
            }
        }

        if (HasValue(filters, "orderDirection"))
        {
            var orderDirection = GetString(filters, "orderDirection");
            if (!string.IsNullOrWhiteSpace(orderDirection) && !schema.AllowedSortDirections.Contains(orderDirection, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"orderDirection '{orderDirection}' is not allowed. Allowed: {string.Join(", ", schema.AllowedSortDirections)}.");
            }
        }

        if (HasValue(filters, "orientation"))
        {
            var orientation = GetString(filters, "orientation");
            if (!string.IsNullOrWhiteSpace(orientation) && !schema.AllowedOrientations.Contains(orientation, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"orientation '{orientation}' is not allowed. Allowed: {string.Join(", ", schema.AllowedOrientations)}.");
            }
        }

        return errors;
    }

    private static bool HasValue(JsonElement json, string propertyName)
    {
        if (!json.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => true
        };
    }

    private static string? GetString(JsonElement json, string propertyName)
    {
        if (!json.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
    }

    private static decimal? GetNumber(JsonElement json, string propertyName)
    {
        if (!json.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;

        return value.TryGetDecimal(out var number) ? number : null;
    }

    private static bool IsAllowedEnumValue(JsonElement json, string propertyName, IReadOnlyCollection<string> allowedValues)
    {
        if (!json.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return false;

        var current = value.GetString();
        if (string.IsNullOrWhiteSpace(current))
            return false;

        return allowedValues.Contains(current, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<ReportDatasetType, ReportDatasetSpecification> BuildDatasetCatalog()
    {
        return new Dictionary<ReportDatasetType, ReportDatasetSpecification>
        {
            [ReportDatasetType.SoftwareInventory] = new(
                Fields:
                [
                    "clientName", "siteName", "agentId", "agentHostname", "automaticLabels", "softwareName", "publisher", "version", "lastSeenAt"
                ],
                ExecutionSchema: new ReportExecutionSchema(
                    ScopeType: ReportScopeType.ClientSiteAgent,
                    DateMode: ReportDateMode.None,
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "landscape",
                    AllowedSortFields: GetEnumNamesAsCamelCase<SoftwareInventoryOrderBy>(),
                    DefaultSortField: ToCamelCase(nameof(SoftwareInventoryOrderBy.SoftwareName)),
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "asc",
                    Filters:
                    [
                        new("clientId", "Cliente", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por cliente.", UiComponent: ReportFilterUiComponent.GuidInput, Placeholder: "GUID do cliente"),
                        new("siteId", "Site", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por site.", UiComponent: ReportFilterUiComponent.GuidInput, DependsOn: "clientId", Placeholder: "GUID do site"),
                        new("agentId", "Agente", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por agente.", UiComponent: ReportFilterUiComponent.GuidInput, DependsOn: "siteId", Placeholder: "GUID do agente"),
                        new("softwareName", "Software", ReportFilterFieldType.Text, false, "Filtros", "Busca parcial por nome do software.", UiComponent: ReportFilterUiComponent.TextSearch, Placeholder: "Ex.: Microsoft Office", MaxLength: 200, IsPartialMatch: true),
                        new("publisher", "Fabricante", ReportFilterFieldType.Text, false, "Filtros", "Busca parcial por fabricante.", UiComponent: ReportFilterUiComponent.TextSearch, Placeholder: "Ex.: Microsoft", MaxLength: 200, IsPartialMatch: true),
                        new("version", "Versao", ReportFilterFieldType.Text, false, "Filtros", "Filtra por versao exata ou parcial.", UiComponent: ReportFilterUiComponent.TextInput, Placeholder: "Ex.: 23H2", MaxLength: 100, IsPartialMatch: true),
                        new("orderBy", "Ordenar por", ReportFilterFieldType.Enum, false, "Ordenacao", "Campo para ordenacao dos resultados.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: GetEnumNamesAsCamelCase<SoftwareInventoryOrderBy>(), DefaultValue: ToCamelCase(nameof(SoftwareInventoryOrderBy.SoftwareName))),
                        new("orderDirection", "Direcao", ReportFilterFieldType.Enum, false, "Ordenacao", "Direcao da ordenacao.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["asc", "desc"], DefaultValue: "asc"),
                        new("orientation", "Orientacao", ReportFilterFieldType.Enum, false, "Formatacao", "Orientacao da pagina do relatorio.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["landscape", "portrait"], DefaultValue: "landscape"),
                        new("limit", "Limite de linhas", ReportFilterFieldType.Integer, false, "Saida", "Maximo recomendado: 10000.", UiComponent: ReportFilterUiComponent.NumberInput, DefaultValue: "1000", Min: 1, Max: 10000)
                    ],
                    SampleFilterPresets:
                    [
                        new ReportFilterPreset("Inventario completo", "Todos os clientes, ordenado por software", "{\"limit\":5000,\"orderBy\":\"softwareName\",\"orderDirection\":\"asc\",\"orientation\":\"landscape\"}"),
                        new ReportFilterPreset("Por cliente/site", "Escopo especifico com ordenacao por ultima visualizacao", "{\"clientId\":\"<guid>\",\"siteId\":\"<guid>\",\"limit\":3000,\"orderBy\":\"lastSeenAt\",\"orderDirection\":\"desc\"}")
                    ])),

            [ReportDatasetType.Logs] = new(
                Fields:
                [
                    "clientId", "siteId", "agentId", "type", "level", "source", "from", "to", "message"
                ],
                ExecutionSchema: new ReportExecutionSchema(
                    ScopeType: ReportScopeType.ClientSiteAgent,
                    DateMode: ReportDateMode.RequiredRange,
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "portrait",
                    AllowedSortFields: GetEnumNamesAsCamelCase<LogsOrderBy>(),
                    DefaultSortField: ToCamelCase(nameof(LogsOrderBy.Timestamp)),
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "desc",
                    Filters:
                    [
                        new("from", "Data inicial", ReportFilterFieldType.DateTime, true, "Periodo", "Inicio do intervalo de logs (obrigatorio).", UiComponent: ReportFilterUiComponent.DateTimePicker),
                        new("to", "Data final", ReportFilterFieldType.DateTime, true, "Periodo", "Fim do intervalo de logs (obrigatorio).", UiComponent: ReportFilterUiComponent.DateTimePicker),
                        new("clientId", "Cliente", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por cliente.", UiComponent: ReportFilterUiComponent.GuidInput, Placeholder: "GUID do cliente"),
                        new("siteId", "Site", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por site.", UiComponent: ReportFilterUiComponent.GuidInput, DependsOn: "clientId", Placeholder: "GUID do site"),
                        new("agentId", "Agente", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por agente.", UiComponent: ReportFilterUiComponent.GuidInput, DependsOn: "siteId", Placeholder: "GUID do agente"),
                        new("type", "Tipo", ReportFilterFieldType.Text, false, "Filtros", "Tipo de log.", UiComponent: ReportFilterUiComponent.TextInput, Placeholder: "Ex.: Agent", MaxLength: 100),
                        new("level", "Nivel", ReportFilterFieldType.Enum, false, "Filtros", "Nivel do log.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["Trace", "Debug", "Info", "Warning", "Error", "Critical"]),
                        new("source", "Origem", ReportFilterFieldType.Text, false, "Filtros", "Origem do evento.", UiComponent: ReportFilterUiComponent.TextInput, Placeholder: "Ex.: System", MaxLength: 100),
                        new("message", "Mensagem", ReportFilterFieldType.Text, false, "Filtros", "Busca parcial no texto da mensagem.", UiComponent: ReportFilterUiComponent.TextSearch, Placeholder: "Texto contido na mensagem", MaxLength: 1000, IsPartialMatch: true),
                        new("orderBy", "Ordenar por", ReportFilterFieldType.Enum, false, "Ordenacao", "Campo para ordenacao dos resultados.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: GetEnumNamesAsCamelCase<LogsOrderBy>(), DefaultValue: ToCamelCase(nameof(LogsOrderBy.Timestamp))),
                        new("orderDirection", "Direcao", ReportFilterFieldType.Enum, false, "Ordenacao", "Direcao da ordenacao.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["asc", "desc"], DefaultValue: "desc"),
                        new("orientation", "Orientacao", ReportFilterFieldType.Enum, false, "Formatacao", "Orientacao da pagina do relatorio.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["landscape", "portrait"], DefaultValue: "portrait"),
                        new("limit", "Limite de linhas", ReportFilterFieldType.Integer, false, "Saida", "Maximo recomendado: 10000.", UiComponent: ReportFilterUiComponent.NumberInput, DefaultValue: "1000", Min: 1, Max: 10000)
                    ],
                    SampleFilterPresets:
                    [
                        new ReportFilterPreset("Ultimas 24h", "Faixa curta para troubleshooting rapido", "{\"from\":\"2026-03-06T00:00:00Z\",\"to\":\"2026-03-07T00:00:00Z\",\"limit\":5000,\"orderBy\":\"timestamp\",\"orderDirection\":\"desc\"}"),
                        new ReportFilterPreset("Ultimos 7 dias por cliente", "Faixa semanal com escopo de cliente", "{\"clientId\":\"<guid>\",\"from\":\"2026-03-01T00:00:00Z\",\"to\":\"2026-03-07T00:00:00Z\",\"limit\":10000,\"orderBy\":\"timestamp\",\"orderDirection\":\"desc\"}")
                    ])),

            [ReportDatasetType.ConfigurationAudit] = new(
                Fields:
                [
                    "entityType", "entityId", "fieldName", "changedBy", "changedAt", "reason"
                ],
                ExecutionSchema: new ReportExecutionSchema(
                    ScopeType: ReportScopeType.Global,
                    DateMode: ReportDateMode.RequiredRange,
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "portrait",
                    AllowedSortFields: GetEnumNamesAsCamelCase<ConfigurationAuditOrderBy>(),
                    DefaultSortField: ToCamelCase(nameof(ConfigurationAuditOrderBy.Timestamp)),
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "desc",
                    Filters:
                    [
                        new("from", "Data inicial", ReportFilterFieldType.DateTime, true, "Periodo", "Inicio do intervalo de auditoria (obrigatorio).", UiComponent: ReportFilterUiComponent.DateTimePicker),
                        new("to", "Data final", ReportFilterFieldType.DateTime, true, "Periodo", "Fim do intervalo de auditoria (obrigatorio).", UiComponent: ReportFilterUiComponent.DateTimePicker),
                        new("entityType", "Tipo de entidade", ReportFilterFieldType.Text, false, "Filtros", "Ex.: Client, Site, ServerConfiguration.", UiComponent: ReportFilterUiComponent.TextInput, Placeholder: "Ex.: Site", MaxLength: 100),
                        new("entityId", "ID da entidade", ReportFilterFieldType.Guid, false, "Filtros", "Filtrar por entidade especifica.", UiComponent: ReportFilterUiComponent.GuidInput, Placeholder: "GUID da entidade"),
                        new("fieldName", "Campo alterado", ReportFilterFieldType.Text, false, "Filtros", "Nome do campo alterado.", UiComponent: ReportFilterUiComponent.TextInput, Placeholder: "Ex.: RetentionDays", MaxLength: 100),
                        new("changedBy", "Alterado por", ReportFilterFieldType.Text, false, "Filtros", "Usuario responsavel pela alteracao.", UiComponent: ReportFilterUiComponent.TextInput, Placeholder: "Ex.: admin@empresa.com", MaxLength: 256),
                        new("reason", "Motivo", ReportFilterFieldType.Text, false, "Filtros", "Texto livre de motivo da alteracao.", UiComponent: ReportFilterUiComponent.TextSearch, Placeholder: "Ex.: Ajuste de politica", MaxLength: 1000, IsPartialMatch: true),
                        new("orderBy", "Ordenar por", ReportFilterFieldType.Enum, false, "Ordenacao", "Campo para ordenacao dos resultados.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: GetEnumNamesAsCamelCase<ConfigurationAuditOrderBy>(), DefaultValue: ToCamelCase(nameof(ConfigurationAuditOrderBy.Timestamp))),
                        new("orderDirection", "Direcao", ReportFilterFieldType.Enum, false, "Ordenacao", "Direcao da ordenacao.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["asc", "desc"], DefaultValue: "desc"),
                        new("orientation", "Orientacao", ReportFilterFieldType.Enum, false, "Formatacao", "Orientacao da pagina do relatorio.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["landscape", "portrait"], DefaultValue: "portrait"),
                        new("limit", "Limite de linhas", ReportFilterFieldType.Integer, false, "Saida", "Maximo recomendado: 10000.", UiComponent: ReportFilterUiComponent.NumberInput, DefaultValue: "1000", Min: 1, Max: 10000)
                    ],
                    SampleFilterPresets:
                    [
                        new ReportFilterPreset("Mes atual", "Auditoria mensal em ordem decrescente", "{\"from\":\"2026-03-01T00:00:00Z\",\"to\":\"2026-03-31T23:59:59Z\",\"limit\":10000,\"orderBy\":\"timestamp\",\"orderDirection\":\"desc\"}")
                    ])),

            [ReportDatasetType.Tickets] = new(
                Fields:
                [
                    "clientId", "siteId", "agentId", "workflowStateId", "priority", "createdAt", "closedAt", "slaBreached"
                ],
                ExecutionSchema: new ReportExecutionSchema(
                    ScopeType: ReportScopeType.ClientSiteAgent,
                    DateMode: ReportDateMode.OptionalRange,
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "landscape",
                    AllowedSortFields: GetEnumNamesAsCamelCase<TicketsOrderBy>(),
                    DefaultSortField: ToCamelCase(nameof(TicketsOrderBy.Timestamp)),
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "desc",
                    Filters:
                    [
                        new("clientId", "Cliente", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por cliente.", UiComponent: ReportFilterUiComponent.GuidInput, Placeholder: "GUID do cliente"),
                        new("siteId", "Site", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por site.", UiComponent: ReportFilterUiComponent.GuidInput, DependsOn: "clientId", Placeholder: "GUID do site"),
                        new("agentId", "Agente", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por agente.", UiComponent: ReportFilterUiComponent.GuidInput, DependsOn: "siteId", Placeholder: "GUID do agente"),
                        new("workflowStateId", "Status workflow", ReportFilterFieldType.Guid, false, "Filtros", "Estado atual do ticket.", UiComponent: ReportFilterUiComponent.GuidInput, Placeholder: "GUID do estado"),
                        new("priority", "Prioridade", ReportFilterFieldType.Enum, false, "Filtros", "Prioridade do ticket.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["Low", "Medium", "High", "Critical"]),
                        new("slaBreached", "SLA violado", ReportFilterFieldType.Boolean, false, "Filtros", "true/false.", UiComponent: ReportFilterUiComponent.Toggle),
                        new("from", "Data inicial", ReportFilterFieldType.DateTime, false, "Periodo", "Inicio opcional do periodo.", UiComponent: ReportFilterUiComponent.DateTimePicker),
                        new("to", "Data final", ReportFilterFieldType.DateTime, false, "Periodo", "Fim opcional do periodo.", UiComponent: ReportFilterUiComponent.DateTimePicker),
                        new("orderBy", "Ordenar por", ReportFilterFieldType.Enum, false, "Ordenacao", "Campo para ordenacao dos resultados.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: GetEnumNamesAsCamelCase<TicketsOrderBy>(), DefaultValue: ToCamelCase(nameof(TicketsOrderBy.Timestamp))),
                        new("orderDirection", "Direcao", ReportFilterFieldType.Enum, false, "Ordenacao", "Direcao da ordenacao.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["asc", "desc"], DefaultValue: "desc"),
                        new("orientation", "Orientacao", ReportFilterFieldType.Enum, false, "Formatacao", "Orientacao da pagina do relatorio.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["landscape", "portrait"], DefaultValue: "landscape"),
                        new("limit", "Limite de linhas", ReportFilterFieldType.Integer, false, "Saida", "Maximo recomendado: 10000.", UiComponent: ReportFilterUiComponent.NumberInput, DefaultValue: "1000", Min: 1, Max: 10000)
                    ],
                    SampleFilterPresets:
                    [
                        new ReportFilterPreset("Tickets abertos recentes", "Foco no fluxo operacional dos ultimos 7 dias", "{\"from\":\"2026-03-01T00:00:00Z\",\"to\":\"2026-03-07T00:00:00Z\",\"orderBy\":\"timestamp\",\"orderDirection\":\"desc\",\"limit\":5000}"),
                        new ReportFilterPreset("Prioridade critica", "Acompanhamento de tickets com impacto alto", "{\"priority\":\"Critical\",\"orderBy\":\"priority\",\"orderDirection\":\"asc\",\"limit\":1000}")
                    ])),

            [ReportDatasetType.AgentHardware] = new(
                Fields:
                [
                    "clientName", "siteName", "agentId", "agentHostname", "automaticLabels", "osName", "osVersion", "osBuild", "osArchitecture", "processor", "processorCores", "processorThreads", "processorArchitecture", "processorFrequencyGhz", "processorSocket", "processorTdpWatts", "totalMemoryGB", "totalMemoryBytes", "gpuModel", "gpuMemoryGB", "gpuDriverVersion", "totalDisksCount", "motherboardManufacturer", "motherboardModel", "biosVersion", "biosManufacturer", "biosDate", "biosSerialNumber", "inventorySchemaVersion", "inventoryCollectedAt", "collectedAt"
                ],
                ExecutionSchema: new ReportExecutionSchema(
                    ScopeType: ReportScopeType.ClientSiteAgent,
                    DateMode: ReportDateMode.None,
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "landscape",
                    AllowedSortFields: GetEnumNamesAsCamelCase<AgentHardwareOrderBy>(),
                    DefaultSortField: ToCamelCase(nameof(AgentHardwareOrderBy.SiteName)),
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "asc",
                    Filters:
                    [
                        new("clientId", "Cliente", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por cliente.", UiComponent: ReportFilterUiComponent.GuidInput, Placeholder: "GUID do cliente"),
                        new("siteId", "Site", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por site.", UiComponent: ReportFilterUiComponent.GuidInput, DependsOn: "clientId", Placeholder: "GUID do site"),
                        new("agentId", "Agente", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por agente.", UiComponent: ReportFilterUiComponent.GuidInput, DependsOn: "siteId", Placeholder: "GUID do agente"),
                        new("osName", "Sistema operacional", ReportFilterFieldType.Text, false, "Filtros", "Filtra por nome do SO.", UiComponent: ReportFilterUiComponent.TextSearch, Placeholder: "Ex.: Windows 11", MaxLength: 150, IsPartialMatch: true),
                        new("processor", "Processador", ReportFilterFieldType.Text, false, "Filtros", "Filtra por processador.", UiComponent: ReportFilterUiComponent.TextSearch, Placeholder: "Ex.: Intel", MaxLength: 200, IsPartialMatch: true),
                        new("orderBy", "Ordenar por", ReportFilterFieldType.Enum, false, "Ordenacao", "Campo para ordenacao dos resultados.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: GetEnumNamesAsCamelCase<AgentHardwareOrderBy>(), DefaultValue: ToCamelCase(nameof(AgentHardwareOrderBy.SiteName))),
                        new("orderDirection", "Direcao", ReportFilterFieldType.Enum, false, "Ordenacao", "Direcao da ordenacao.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["asc", "desc"], DefaultValue: "asc"),
                        new("orientation", "Orientacao", ReportFilterFieldType.Enum, false, "Formatacao", "Orientacao da pagina do relatorio.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["landscape", "portrait"], DefaultValue: "landscape"),
                        new("limit", "Limite de linhas", ReportFilterFieldType.Integer, false, "Saida", "Maximo recomendado: 10000.", UiComponent: ReportFilterUiComponent.NumberInput, DefaultValue: "1000", Min: 1, Max: 10000)
                    ],
                    SampleFilterPresets:
                    [
                        new ReportFilterPreset("Inventario geral", "Visao consolidada de hardware de todos os agentes", "{\"limit\":5000,\"orderBy\":\"siteName\",\"orderDirection\":\"asc\",\"orientation\":\"landscape\"}"),
                        new ReportFilterPreset("Por cliente com detalhes", "Inventario detalhado de hardware por cliente, ordenado por hostname", "{\"clientId\":\"<guid>\",\"limit\":3000,\"orderBy\":\"agentHostname\",\"orderDirection\":\"asc\",\"orientation\":\"landscape\"}"),
                        new ReportFilterPreset("Ultima coleta", "Agentes ordenados por data de coleta mais recente", "{\"limit\":5000,\"orderBy\":\"collectedAt\",\"orderDirection\":\"desc\",\"orientation\":\"landscape\"}")
                    ])),

            [ReportDatasetType.AgentInventoryComposite] = new(
                Fields:
                [
                    "clientName", "siteName", "agentId", "agentHostname", "automaticLabels", "osName", "osVersion", "processor", "totalMemoryGB", "totalDisksCount", "softwareName", "publisher", "softwareVersion", "softwareLastSeenAt", "hardwareCollectedAt"
                ],
                ExecutionSchema: new ReportExecutionSchema(
                    ScopeType: ReportScopeType.ClientSiteAgent,
                    DateMode: ReportDateMode.None,
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "landscape",
                    AllowedSortFields: GetEnumNamesAsCamelCase<AgentInventoryCompositeOrderBy>(),
                    DefaultSortField: ToCamelCase(nameof(AgentInventoryCompositeOrderBy.AgentHostname)),
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "asc",
                    Filters:
                    [
                        new("clientId", "Cliente", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por cliente.", UiComponent: ReportFilterUiComponent.GuidInput, Placeholder: "GUID do cliente"),
                        new("siteId", "Site", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por site.", UiComponent: ReportFilterUiComponent.GuidInput, DependsOn: "clientId", Placeholder: "GUID do site"),
                        new("agentId", "Agente", ReportFilterFieldType.Guid, false, "Escopo", "Escopo opcional por agente.", UiComponent: ReportFilterUiComponent.GuidInput, DependsOn: "siteId", Placeholder: "GUID do agente"),
                        new("softwareName", "Software", ReportFilterFieldType.Text, false, "Filtros", "Busca parcial por nome do software.", UiComponent: ReportFilterUiComponent.TextSearch, Placeholder: "Ex.: Microsoft Office", MaxLength: 200, IsPartialMatch: true),
                        new("orderBy", "Ordenar por", ReportFilterFieldType.Enum, false, "Ordenacao", "Campo para ordenacao dos resultados.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: GetEnumNamesAsCamelCase<AgentInventoryCompositeOrderBy>(), DefaultValue: ToCamelCase(nameof(AgentInventoryCompositeOrderBy.AgentHostname))),
                        new("orderDirection", "Direcao", ReportFilterFieldType.Enum, false, "Ordenacao", "Direcao da ordenacao.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["asc", "desc"], DefaultValue: "asc"),
                        new("orientation", "Orientacao", ReportFilterFieldType.Enum, false, "Formatacao", "Orientacao da pagina do relatorio.", UiComponent: ReportFilterUiComponent.Select, AllowedValues: ["landscape", "portrait"], DefaultValue: "landscape"),
                        new("limit", "Limite de linhas", ReportFilterFieldType.Integer, false, "Saida", "Maximo recomendado: 10000.", UiComponent: ReportFilterUiComponent.NumberInput, DefaultValue: "1000", Min: 1, Max: 10000)
                    ],
                    SampleFilterPresets:
                    [
                        new ReportFilterPreset("Hardware + software por agente", "Combina dados basicos de hardware com lista de softwares por agente", "{\"limit\":5000,\"orderBy\":\"agentHostname\",\"orderDirection\":\"asc\",\"orientation\":\"landscape\"}"),
                        new ReportFilterPreset("Por cliente", "Inventario composto com escopo de cliente", "{\"clientId\":\"<guid>\",\"limit\":3000,\"orderBy\":\"siteName\",\"orderDirection\":\"asc\"}")
                    ]))
        };
    }

    private static string[] GetEnumNamesAsCamelCase<TEnum>() where TEnum : struct, Enum
    {
        return Enum.GetNames<TEnum>().Select(ToCamelCase).ToArray();
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value) || char.IsLower(value[0]))
            return value;

        return char.ToLowerInvariant(value[0]) + value.Substring(1);
    }

    private static string GetDatasetDisplayName(ReportDatasetType datasetType)
    {
        return datasetType switch
        {
            ReportDatasetType.SoftwareInventory => "Software Inventory",
            ReportDatasetType.Logs => "System Logs",
            ReportDatasetType.ConfigurationAudit => "Configuration Audit",
            ReportDatasetType.Tickets => "Tickets",
            ReportDatasetType.AgentHardware => "Agent Hardware",
            ReportDatasetType.AgentInventoryComposite => "Agent Inventory Composite",
            _ => datasetType.ToString()
        };
    }

    private static string GetDatasetDescription(ReportDatasetType datasetType)
    {
        return datasetType switch
        {
            ReportDatasetType.SoftwareInventory => "Installed software by agent/site/client.",
            ReportDatasetType.Logs => "Operational logs with period and severity filters.",
            ReportDatasetType.ConfigurationAudit => "Configuration change history and audit trail.",
            ReportDatasetType.Tickets => "Ticket lifecycle, SLA and workflow data.",
            ReportDatasetType.AgentHardware => "Hardware inventory and system specs by agent.",
            ReportDatasetType.AgentInventoryComposite => "Pre-joined hardware and software inventory by agent.",
            _ => "Dataset available for reporting."
        };
    }

    private static bool IsJoinKeyField(string field)
    {
        return field.Equals("clientId", StringComparison.OrdinalIgnoreCase)
            || field.Equals("siteId", StringComparison.OrdinalIgnoreCase)
            || field.Equals("agentId", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferFieldType(string field)
    {
        if (field.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
            return "guid";
        if (field.EndsWith("At", StringComparison.OrdinalIgnoreCase) || field.EndsWith("Date", StringComparison.OrdinalIgnoreCase))
            return "datetime";
        if (field.Contains("count", StringComparison.OrdinalIgnoreCase) || field.Contains("bytes", StringComparison.OrdinalIgnoreCase) || field.Contains("gb", StringComparison.OrdinalIgnoreCase))
            return "number";
        if (field.StartsWith("is", StringComparison.OrdinalIgnoreCase) || field.EndsWith("Breached", StringComparison.OrdinalIgnoreCase))
            return "boolean";

        return "text";
    }

    private static string GetDefaultAlias(ReportDatasetType datasetType)
    {
        return datasetType switch
        {
            ReportDatasetType.SoftwareInventory => "sw",
            ReportDatasetType.AgentHardware => "hw",
            ReportDatasetType.Logs => "log",
            ReportDatasetType.ConfigurationAudit => "audit",
            ReportDatasetType.Tickets => "tk",
            ReportDatasetType.AgentInventoryComposite => "inv",
            _ => "src"
        };
    }
}

public record CreateReportTemplateRequest(
    string Name,
    string? Description,
    string? Instructions,
    string? ExecutionSchemaJson,
    ReportDatasetType DatasetType,
    ReportFormat DefaultFormat,
    string LayoutJson,
    string? FiltersJson,
    string? CreatedBy);

public record UpdateReportTemplateRequest(
    string? Name,
    string? Description,
    string? Instructions,
    string? ExecutionSchemaJson,
    ReportDatasetType? DatasetType,
    ReportFormat? DefaultFormat,
    string? LayoutJson,
    string? FiltersJson,
    bool? IsActive,
    string? UpdatedBy);

public record RunReportRequest(
    Guid TemplateId,
    ReportFormat? Format,
    string? FiltersJson,
    string? CreatedBy,
    bool RunAsync = false);

public record PreviewReportRequest(
    Guid? TemplateId,
    PreviewReportTemplateDraft? Template,
    ReportFormat? Format,
    string? FiltersJson,
    string? FileName,
    string? ResponseDisposition = "inline",
    string? PreviewMode = "document");

public record PreviewReportTemplateDraft(
    string? Name,
    string? Description,
    string? Instructions,
    string? ExecutionSchemaJson,
    ReportDatasetType? DatasetType,
    ReportFormat? DefaultFormat,
    string? LayoutJson,
    string? FiltersJson);

public record ReportTemplateResponse(
    Guid Id,
    Guid? ClientId,
    string Name,
    string? Description,
    string? Instructions,
    ReportDatasetType DatasetType,
    ReportFormat DefaultFormat,
    string LayoutJson,
    string? FiltersJson,
    bool IsActive,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy,
    ReportExecutionSchema? ExecutionSchema,
    string? ExecutionSchemaJson);

public record ReportDatasetSpecification(
    string[] Fields,
    ReportExecutionSchema ExecutionSchema);

public record ReportExecutionSchema(
    ReportScopeType ScopeType,
    ReportDateMode DateMode,
    string[] AllowedOrientations,
    string DefaultOrientation,
    string[] AllowedSortFields,
    string DefaultSortField,
    string[] AllowedSortDirections,
    string DefaultSortDirection,
    IReadOnlyList<ReportFilterDefinition> Filters,
    IReadOnlyList<ReportFilterPreset>? SampleFilterPresets);

public record ReportFilterDefinition(
    string Name,
    string Label,
    ReportFilterFieldType Type,
    bool Required,
    string? Group,
    string? Description,
    ReportFilterUiComponent? UiComponent = null,
    string? DependsOn = null,
    string? DefaultValue = null,
    string? Placeholder = null,
    string[]? AllowedValues = null,
    string[]? AllowedValueLabels = null,
    decimal? Min = null,
    decimal? Max = null,
    int? MaxLength = null,
    bool IsPartialMatch = false);

public record ReportFilterPreset(
    string Name,
    string Description,
    string FiltersJson);

// ── Schedule request/response records ──

public record CreateReportScheduleRequest(
    Guid TemplateId,
    ReportFormat Format = ReportFormat.Pdf,
    string? Label = null,
    string? CronExpression = "0 8 * * 1",
    string? TimeZoneId = "UTC",
    int? MaxRetainedExecutions = 10,
    bool? IsActive = true,
    string? FiltersJson = null,
    string? CreatedBy = null);

public record UpdateReportScheduleRequest(
    string? Label = null,
    string? CronExpression = null,
    string? TimeZoneId = null,
    int? MaxRetainedExecutions = null,
    bool? IsActive = null,
    string? FiltersJson = null,
    string? UpdatedBy = null,
    Guid? ClientId = null);

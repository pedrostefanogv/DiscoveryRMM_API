using Meduza.Core.Configuration;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly IReportTemplateRepository _templateRepository;
    private readonly IReportExecutionRepository _executionRepository;
    private readonly IReportService _reportService;
    private readonly ReportFormat[] _enabledFormats;

    public ReportsController(
        IReportTemplateRepository templateRepository,
        IReportExecutionRepository executionRepository,
        IReportService reportService,
        IOptions<ReportingOptions> reportingOptions)
    {
        _templateRepository = templateRepository;
        _executionRepository = executionRepository;
        _reportService = reportService;

        var options = reportingOptions.Value;
        _enabledFormats = options.EnablePdf
            ? [ReportFormat.Xlsx, ReportFormat.Csv, ReportFormat.Pdf]
            : [ReportFormat.Xlsx, ReportFormat.Csv];
    }

    [HttpGet("datasets")]
    public IActionResult GetDatasetCatalog()
    {
        var datasets = new[]
        {
            new
            {
                type = ReportDatasetType.SoftwareInventory,
                fields = new[] { "clientId", "siteId", "agentId", "softwareName", "publisher", "version", "installedAt" },
                formats = _enabledFormats
            },
            new
            {
                type = ReportDatasetType.Logs,
                fields = new[] { "clientId", "siteId", "agentId", "type", "level", "source", "from", "to", "message" },
                formats = _enabledFormats
            },
            new
            {
                type = ReportDatasetType.ConfigurationAudit,
                fields = new[] { "entityType", "entityId", "fieldName", "changedBy", "changedAt", "reason" },
                formats = _enabledFormats
            },
            new
            {
                type = ReportDatasetType.Tickets,
                fields = new[] { "clientId", "siteId", "agentId", "workflowStateId", "priority", "createdAt", "closedAt", "slaBreached" },
                formats = _enabledFormats
            },
            new
            {
                type = ReportDatasetType.AgentHardware,
                fields = new[] { "clientId", "siteId", "agentId", "osName", "processor", "totalMemoryBytes", "collectedAt" },
                formats = _enabledFormats
            }
        };

        return Ok(datasets);
    }

    [HttpPost("templates")]
    public async Task<IActionResult> CreateTemplate([FromBody] CreateReportTemplateRequest request)
    {
        var template = new ReportTemplate
        {
            ClientId = null,
            Name = request.Name,
            Description = request.Description,
            DatasetType = request.DatasetType,
            DefaultFormat = request.DefaultFormat,
            LayoutJson = request.LayoutJson,
            FiltersJson = request.FiltersJson,
            IsActive = true,
            CreatedBy = request.CreatedBy,
            UpdatedBy = request.CreatedBy
        };

        var created = await _templateRepository.CreateAsync(template);
        return CreatedAtAction(nameof(GetTemplateById), new { id = created.Id }, created);
    }

    [HttpGet("templates")]
    public async Task<IActionResult> GetTemplates(
        [FromQuery] ReportDatasetType? datasetType,
        [FromQuery] bool? isActive = true)
    {
        var templates = await _templateRepository.GetAllAsync(null, datasetType, isActive);
        return Ok(templates);
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<IActionResult> GetTemplateById(Guid id)
    {
        var template = await _templateRepository.GetByIdAsync(id, null);
        return template is null ? NotFound() : Ok(template);
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
        return Ok(current);
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
            ClientId = TryGetGuidFromFilters(request.FiltersJson ?? template.FiltersJson, "clientId"),
            Format = selectedFormat,
            FiltersJson = request.FiltersJson ?? template.FiltersJson,
            Status = ReportExecutionStatus.Pending,
            CreatedBy = request.CreatedBy
        };

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
            contentType = processed.ResultContentType,
            resultSizeBytes = processed.ResultSizeBytes,
            downloadPath
        });
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
        var result = await _reportService.GetDownloadAsync(id, clientId);
        if (result is null)
            return NotFound(new { error = "Report file not available." });

        return File(result.Value.Content, result.Value.ContentType, result.Value.FileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// Stream download (for large files without loading entire file into memory)
    /// </summary>
    [HttpGet("executions/{id:guid}/download-stream")]
    public async Task<IActionResult> DownloadExecutionStream(Guid id, [FromQuery] Guid? clientId)
    {
        var execution = await _executionRepository.GetByIdAsync(id, clientId);
        if (execution is null || execution.Status != ReportExecutionStatus.Completed || string.IsNullOrWhiteSpace(execution.ResultPath))
            return NotFound(new { error = "Report file not available." });

        var filePath = execution.ResultPath;
        if (!System.IO.File.Exists(filePath))
            return NotFound(new { error = "Report file not found on disk." });

        var fileStream = System.IO.File.OpenRead(filePath);
        var fileName = Path.GetFileName(filePath);
        var contentType = string.IsNullOrWhiteSpace(execution.ResultContentType) 
            ? "application/octet-stream" 
            : execution.ResultContentType;

        return File(fileStream, contentType, fileName, enableRangeProcessing: true);
    }

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
}

public record CreateReportTemplateRequest(
    string Name,
    string? Description,
    ReportDatasetType DatasetType,
    ReportFormat DefaultFormat,
    string LayoutJson,
    string? FiltersJson,
    string? CreatedBy);

public record UpdateReportTemplateRequest(
    string? Name,
    string? Description,
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

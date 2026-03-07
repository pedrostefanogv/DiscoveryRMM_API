using Meduza.Core.Configuration;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
            ClientId = request.ClientId,
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
        return CreatedAtAction(nameof(GetTemplateById), new { id = created.Id, clientId = request.ClientId }, created);
    }

    [HttpGet("templates")]
    public async Task<IActionResult> GetTemplates(
        [FromQuery] Guid? clientId,
        [FromQuery] ReportDatasetType? datasetType,
        [FromQuery] bool? isActive = true)
    {
        var templates = await _templateRepository.GetAllAsync(clientId, datasetType, isActive);
        return Ok(templates);
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<IActionResult> GetTemplateById(Guid id, [FromQuery] Guid? clientId)
    {
        var template = await _templateRepository.GetByIdAsync(id, clientId);
        return template is null ? NotFound() : Ok(template);
    }

    [HttpPut("templates/{id:guid}")]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] UpdateReportTemplateRequest request)
    {
        var current = await _templateRepository.GetByIdAsync(id, request.ClientId);
        if (current is null)
            return NotFound();

        current.Name = request.Name;
        current.Description = request.Description;
        current.DatasetType = request.DatasetType;
        current.DefaultFormat = request.DefaultFormat;
        current.LayoutJson = request.LayoutJson;
        current.FiltersJson = request.FiltersJson;
        current.IsActive = request.IsActive;
        current.UpdatedBy = request.UpdatedBy;

        await _templateRepository.UpdateAsync(current);
        return Ok(current);
    }

    [HttpDelete("templates/{id:guid}")]
    public async Task<IActionResult> DeleteTemplate(Guid id, [FromQuery] Guid? clientId)
    {
        var deleted = await _templateRepository.DeleteAsync(id, clientId);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("run")]
    public async Task<IActionResult> RunReport([FromBody] RunReportRequest request)
    {
        var template = await _templateRepository.GetByIdAsync(request.TemplateId, request.ClientId);
        if (template is null)
            return NotFound(new { error = "Template not found for client." });

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
            ClientId = request.ClientId,
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

        return Ok(new
        {
            executionId = processed.Id,
            status = processed.Status,
            rowCount = processed.RowCount,
            contentType = processed.ResultContentType,
            resultSizeBytes = processed.ResultSizeBytes,
            downloadPath = $"/api/reports/executions/{processed.Id}/download?clientId={processed.ClientId}"
        });
    }

    [HttpGet("executions/{id:guid}")]
    public async Task<IActionResult> GetExecutionById(Guid id, [FromQuery] Guid clientId)
    {
        var execution = await _executionRepository.GetByIdAsync(id, clientId);
        return execution is null ? NotFound() : Ok(execution);
    }

    [HttpGet("executions")]
    public async Task<IActionResult> GetExecutions([FromQuery] Guid clientId, [FromQuery] int limit = 50)
    {
        var executions = await _executionRepository.GetRecentByClientAsync(clientId, limit);
        return Ok(executions);
    }

    [HttpGet("executions/{id:guid}/download")]
    public async Task<IActionResult> DownloadExecution(Guid id, [FromQuery] Guid clientId)
    {
        var result = await _reportService.GetDownloadAsync(id, clientId);
        if (result is null)
            return NotFound(new { error = "Report file not available." });

        return File(result.Value.Content, result.Value.ContentType, result.Value.FileName);
    }
}

public record CreateReportTemplateRequest(
    Guid? ClientId,
    string Name,
    string? Description,
    ReportDatasetType DatasetType,
    ReportFormat DefaultFormat,
    string LayoutJson,
    string? FiltersJson,
    string? CreatedBy);

public record UpdateReportTemplateRequest(
    Guid? ClientId,
    string Name,
    string? Description,
    ReportDatasetType DatasetType,
    ReportFormat DefaultFormat,
    string LayoutJson,
    string? FiltersJson,
    bool IsActive,
    string? UpdatedBy);

public record RunReportRequest(
    Guid TemplateId,
    Guid ClientId,
    ReportFormat? Format,
    string? FiltersJson,
    string? CreatedBy,
    bool RunAsync = false);

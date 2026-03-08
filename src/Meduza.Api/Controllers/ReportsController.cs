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
    private static readonly IReadOnlyDictionary<ReportDatasetType, ReportDatasetSpecification> DatasetCatalog = BuildDatasetCatalog();

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
        var datasets = DatasetCatalog
            .Select(item => new
            {
                type = item.Key,
                fields = item.Value.Fields,
                formats = _enabledFormats,
                executionSchema = item.Value.ExecutionSchema
            });

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

    private static ReportTemplateResponse ToTemplateResponse(ReportTemplate template)
    {
        ReportExecutionSchema? executionSchema = null;

        // Prioridade: schema customizado do template > schema padrão do dataset
        if (!string.IsNullOrWhiteSpace(template.ExecutionSchemaJson))
        {
            try
            {
                executionSchema = JsonSerializer.Deserialize<ReportExecutionSchema>(template.ExecutionSchemaJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                // Falha ao deserializar schema customizado, usa padrão do dataset
                DatasetCatalog.TryGetValue(template.DatasetType, out var fallback);
                executionSchema = fallback?.ExecutionSchema;
            }
        }
        else if (DatasetCatalog.TryGetValue(template.DatasetType, out var specification))
        {
            executionSchema = specification.ExecutionSchema;
        }

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

    private static IReadOnlyDictionary<ReportDatasetType, ReportDatasetSpecification> BuildDatasetCatalog()
    {
        return new Dictionary<ReportDatasetType, ReportDatasetSpecification>
        {
            [ReportDatasetType.SoftwareInventory] = new(
                Fields:
                [
                    "clientId", "siteId", "agentId", "softwareName", "publisher", "version", "installedAt"
                ],
                ExecutionSchema: new ReportExecutionSchema(
                    Scope: "global-client-site-agent",
                    DateMode: "none",
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "landscape",
                    AllowedSortFields: ["softwareName", "publisher", "version", "lastSeenAt", "agentHostname", "siteName"],
                    DefaultSortField: "softwareName",
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "asc",
                    Filters:
                    [
                        new("clientId", "Cliente", "guid", false, "Escopo opcional por cliente."),
                        new("siteId", "Site", "guid", false, "Escopo opcional por site."),
                        new("agentId", "Agente", "guid", false, "Escopo opcional por agente."),
                        new("softwareName", "Software", "text", false, "Busca parcial por nome do software."),
                        new("publisher", "Fabricante", "text", false, "Busca parcial por fabricante."),
                        new("version", "Versao", "text", false, "Filtra por versao exata ou parcial."),
                        new("limit", "Limite de linhas", "number", false, "Maximo recomendado: 10000."),
                        new("orderBy", "Ordenar por", "enum", false, "Campo para ordenacao."),
                        new("orderDirection", "Direcao", "enum", false, "asc ou desc."),
                        new("orientation", "Orientacao", "enum", false, "landscape ou portrait para renderizacao.")
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
                    Scope: "global-client-site-agent",
                    DateMode: "date-range",
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "portrait",
                    AllowedSortFields: ["createdAt", "level", "source", "type"],
                    DefaultSortField: "createdAt",
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "desc",
                    Filters:
                    [
                        new("from", "Data inicial", "datetime", true, "Inicio do intervalo de logs (obrigatorio)."),
                        new("to", "Data final", "datetime", true, "Fim do intervalo de logs (obrigatorio)."),
                        new("clientId", "Cliente", "guid", false, "Escopo opcional por cliente."),
                        new("siteId", "Site", "guid", false, "Escopo opcional por site."),
                        new("agentId", "Agente", "guid", false, "Escopo opcional por agente."),
                        new("type", "Tipo", "text", false, "Tipo de log."),
                        new("level", "Nivel", "text", false, "Nivel: Trace/Info/Warning/Error."),
                        new("source", "Origem", "text", false, "Origem do evento."),
                        new("message", "Mensagem", "text", false, "Busca parcial no texto da mensagem."),
                        new("limit", "Limite de linhas", "number", false, "Maximo recomendado: 10000."),
                        new("orderBy", "Ordenar por", "enum", false, "Campo para ordenacao."),
                        new("orderDirection", "Direcao", "enum", false, "asc ou desc.")
                    ],
                    SampleFilterPresets:
                    [
                        new ReportFilterPreset("Ultimas 24h", "Faixa curta para troubleshooting rapido", "{\"from\":\"2026-03-06T00:00:00Z\",\"to\":\"2026-03-07T00:00:00Z\",\"limit\":5000,\"orderBy\":\"createdAt\",\"orderDirection\":\"desc\"}"),
                        new ReportFilterPreset("Ultimos 7 dias por cliente", "Faixa semanal com escopo de cliente", "{\"clientId\":\"<guid>\",\"from\":\"2026-03-01T00:00:00Z\",\"to\":\"2026-03-07T00:00:00Z\",\"limit\":10000,\"orderBy\":\"createdAt\",\"orderDirection\":\"desc\"}")
                    ])),

            [ReportDatasetType.ConfigurationAudit] = new(
                Fields:
                [
                    "entityType", "entityId", "fieldName", "changedBy", "changedAt", "reason"
                ],
                ExecutionSchema: new ReportExecutionSchema(
                    Scope: "global",
                    DateMode: "date-range",
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "portrait",
                    AllowedSortFields: ["changedAt", "entityType", "changedBy", "fieldName"],
                    DefaultSortField: "changedAt",
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "desc",
                    Filters:
                    [
                        new("from", "Data inicial", "datetime", true, "Inicio do intervalo de auditoria (obrigatorio)."),
                        new("to", "Data final", "datetime", true, "Fim do intervalo de auditoria (obrigatorio)."),
                        new("entityType", "Tipo de entidade", "text", false, "Ex.: Client, Site, ServerConfiguration."),
                        new("entityId", "ID da entidade", "guid", false, "Filtrar por entidade especifica."),
                        new("fieldName", "Campo alterado", "text", false, "Nome do campo alterado."),
                        new("changedBy", "Alterado por", "text", false, "Usuario responsavel pela alteracao."),
                        new("reason", "Motivo", "text", false, "Texto livre de motivo da alteracao."),
                        new("limit", "Limite de linhas", "number", false, "Maximo recomendado: 10000."),
                        new("orderBy", "Ordenar por", "enum", false, "Campo para ordenacao."),
                        new("orderDirection", "Direcao", "enum", false, "asc ou desc.")
                    ],
                    SampleFilterPresets:
                    [
                        new ReportFilterPreset("Mes atual", "Auditoria mensal em ordem decrescente", "{\"from\":\"2026-03-01T00:00:00Z\",\"to\":\"2026-03-31T23:59:59Z\",\"limit\":10000,\"orderBy\":\"changedAt\",\"orderDirection\":\"desc\"}")
                    ])),

            [ReportDatasetType.Tickets] = new(
                Fields:
                [
                    "clientId", "siteId", "agentId", "workflowStateId", "priority", "createdAt", "closedAt", "slaBreached"
                ],
                ExecutionSchema: new ReportExecutionSchema(
                    Scope: "global-client-site-agent",
                    DateMode: "optional-date-range",
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "landscape",
                    AllowedSortFields: ["createdAt", "priority", "slaBreached", "closedAt"],
                    DefaultSortField: "createdAt",
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "desc",
                    Filters:
                    [
                        new("clientId", "Cliente", "guid", false, "Escopo opcional por cliente."),
                        new("siteId", "Site", "guid", false, "Escopo opcional por site."),
                        new("agentId", "Agente", "guid", false, "Escopo opcional por agente."),
                        new("workflowStateId", "Status workflow", "guid", false, "Estado atual do ticket."),
                        new("priority", "Prioridade", "text", false, "Ex.: Low, Medium, High, Critical."),
                        new("slaBreached", "SLA violado", "boolean", false, "true/false."),
                        new("from", "Data inicial", "datetime", false, "Inicio opcional do periodo."),
                        new("to", "Data final", "datetime", false, "Fim opcional do periodo."),
                        new("limit", "Limite de linhas", "number", false, "Maximo recomendado: 10000."),
                        new("orderBy", "Ordenar por", "enum", false, "Campo para ordenacao."),
                        new("orderDirection", "Direcao", "enum", false, "asc ou desc.")
                    ],
                    SampleFilterPresets:
                    [
                        new ReportFilterPreset("Tickets abertos recentes", "Foco no fluxo operacional dos ultimos 7 dias", "{\"from\":\"2026-03-01T00:00:00Z\",\"to\":\"2026-03-07T00:00:00Z\",\"orderBy\":\"createdAt\",\"orderDirection\":\"desc\",\"limit\":5000}"),
                        new ReportFilterPreset("Prioridade critica", "Acompanhamento de tickets com impacto alto", "{\"priority\":\"Critical\",\"orderBy\":\"priority\",\"orderDirection\":\"asc\",\"limit\":1000}")
                    ])),

            [ReportDatasetType.AgentHardware] = new(
                Fields:
                [
                    "clientId", "siteId", "agentId", "osName", "processor", "totalMemoryBytes", "collectedAt"
                ],
                ExecutionSchema: new ReportExecutionSchema(
                    Scope: "global-client-site-agent",
                    DateMode: "none",
                    AllowedOrientations: ["landscape", "portrait"],
                    DefaultOrientation: "landscape",
                    AllowedSortFields: ["siteName", "agentHostname", "collectedAt", "osName"],
                    DefaultSortField: "siteName",
                    AllowedSortDirections: ["asc", "desc"],
                    DefaultSortDirection: "asc",
                    Filters:
                    [
                        new("clientId", "Cliente", "guid", false, "Escopo opcional por cliente."),
                        new("siteId", "Site", "guid", false, "Escopo opcional por site."),
                        new("agentId", "Agente", "guid", false, "Escopo opcional por agente."),
                        new("osName", "Sistema operacional", "text", false, "Filtra por nome do SO."),
                        new("processor", "Processador", "text", false, "Filtra por processador."),
                        new("limit", "Limite de linhas", "number", false, "Maximo recomendado: 10000."),
                        new("orderBy", "Ordenar por", "enum", false, "Campo para ordenacao."),
                        new("orderDirection", "Direcao", "enum", false, "asc ou desc.")
                    ],
                    SampleFilterPresets:
                    [
                        new ReportFilterPreset("Inventario geral", "Visao consolidada de hardware", "{\"limit\":5000,\"orderBy\":\"siteName\",\"orderDirection\":\"asc\"}"),
                        new ReportFilterPreset("Ultima coleta por cliente", "Escopo por cliente ordenado por coleta", "{\"clientId\":\"<guid>\",\"limit\":3000,\"orderBy\":\"collectedAt\",\"orderDirection\":\"desc\"}")
                    ]))
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
    string Scope,
    string DateMode,
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
    string Type,
    bool Required,
    string? Description);

public record ReportFilterPreset(
    string Name,
    string Description,
    string FiltersJson);

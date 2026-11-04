using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Exceptions;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// CRUD das definições de sensores (catálogo de monitoramento). A aba
/// "Sensores" do detalhe do agente consome /agents/{id}/sensors; este controller
/// gerencia a definição em si (escopo, limiares, script, canais).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/sensors")]
public class SensorsController(
    ISensorDefinitionService definitions,
    ISensorReadService readService) : ControllerBase
{
    /// <summary>
    /// Agregados da frota (contadores por status/tipo e agentes com mais falhas)
    /// para a página "Sensores" e dashboards. Filtros opcionais de cliente/site.
    /// </summary>
    [HttpGet("summary")]
    [RequirePermission(ResourceType.Sensors, ActionType.View)]
    public async Task<IActionResult> GetSummary(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        CancellationToken cancellationToken = default)
    {
        var summary = await readService.GetFleetSummaryAsync(clientId, siteId, cancellationToken);
        return Ok(summary);
    }

    [HttpGet]
    [RequirePermission(ResourceType.Sensors, ActionType.View)]
    public async Task<IActionResult> GetAll(
        [FromQuery] AppApprovalScopeType? scopeType = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] bool includeInactive = false,
        [FromQuery] string? search = null,
        [FromQuery] SensorType? sensorType = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        // Resposta aditiva: items/count continuam iguais; total/page/pageSize
        // entram para o cliente paginar de verdade (antes cortava em 500).
        var result = await definitions.ListAsync(
            scopeType, clientId, siteId, agentId, includeInactive,
            search, sensorType, isActive, page, pageSize, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Sensors, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var dto = await definitions.GetAsync(id, cancellationToken);
        return dto is null ? NotFound(new { error = "Sensor não encontrado." }) : Ok(dto);
    }

    [HttpPost]
    [RequirePermission(ResourceType.Sensors, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] SensorUpsertRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await definitions.CreateAsync(request, CurrentActor(), cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto);
        }
        catch (SensorValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Sensors, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SensorUpsertRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await definitions.UpdateAsync(id, request, CurrentActor(), cancellationToken);
            return dto is null ? NotFound(new { error = "Sensor não encontrado." }) : Ok(dto);
        }
        catch (SensorValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Sensors, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await definitions.DeleteAsync(id, CurrentActor(), cancellationToken);
        return deleted ? NoContent() : NotFound(new { error = "Sensor não encontrado." });
    }

    private string CurrentActor()
        => HttpContext.Items["UserId"] is Guid userId
            ? $"user:{userId}"
            : HttpContext.Items["Username"] as string ?? "system";
}

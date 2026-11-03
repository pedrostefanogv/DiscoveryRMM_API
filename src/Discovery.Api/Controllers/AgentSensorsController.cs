using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Exceptions;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>Leitura e ações dos sensores de um agente (aba "Sensores" do detalhe).</summary>
[ApiController]
[Route("api/v{version:apiVersion}/agents/{agentId:guid}/sensors")]
public class AgentSensorsController(
    ISensorReadService readService,
    ISensorSeriesService seriesService,
    ISensorOperationService operationService) : ControllerBase
{
    public sealed record SensorRunRequest(IReadOnlyList<Guid>? SensorIds = null, bool ForceSync = false);

    [HttpGet]
    [RequirePermission(ResourceType.Sensors, ActionType.View)]
    public async Task<IActionResult> GetAll(Guid agentId, CancellationToken cancellationToken)
    {
        var payload = await readService.GetForAgentAsync(agentId, cancellationToken);
        return payload is null ? NotFound(new { error = "Agente não encontrado." }) : Ok(payload);
    }

    [HttpGet("{sensorId:guid}/series")]
    [RequirePermission(ResourceType.Sensors, ActionType.View)]
    public async Task<IActionResult> GetSeries(
        Guid agentId,
        Guid sensorId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? channels = null,
        [FromQuery] string? targetKey = null,
        CancellationToken cancellationToken = default)
    {
        var channelList = string.IsNullOrWhiteSpace(channels)
            ? null
            : channels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var series = await seriesService.GetSeriesAsync(
            agentId,
            sensorId,
            targetKey,
            from ?? default,
            to ?? DateTime.UtcNow,
            channelList,
            cancellationToken);

        return series is null ? NotFound(new { error = "Sensor não encontrado." }) : Ok(series);
    }

    [HttpGet("{sensorId:guid}/events")]
    [RequirePermission(ResourceType.Sensors, ActionType.View)]
    public async Task<IActionResult> GetEvents(
        Guid agentId,
        Guid sensorId,
        [FromQuery] string? targetKey = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var page = await seriesService.GetEventsAsync(agentId, sensorId, targetKey, cursor, limit, cancellationToken);
        return Ok(page);
    }

    [HttpPost("run")]
    [RequirePermission(ResourceType.Sensors, ActionType.Execute)]
    public async Task<IActionResult> Run(Guid agentId, [FromBody] SensorRunRequest? request, CancellationToken cancellationToken)
    {
        try
        {
            var response = request?.ForceSync == true
                ? await operationService.SyncNowAsync(agentId, cancellationToken)
                : await operationService.RunAsync(agentId, request?.SensorIds, cancellationToken);

            return response.Dispatched ? Accepted(response) : Conflict(response);
        }
        catch (SensorValidationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("{sensorId:guid}/reset")]
    [RequirePermission(ResourceType.Sensors, ActionType.Execute)]
    public async Task<IActionResult> Reset(Guid agentId, Guid sensorId, CancellationToken cancellationToken)
    {
        var count = await operationService.ResetAsync(agentId, sensorId, cancellationToken);
        return Ok(new { reset = count });
    }

    [HttpPost("sync-now")]
    [RequirePermission(ResourceType.Sensors, ActionType.Execute)]
    public async Task<IActionResult> SyncNow(Guid agentId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await operationService.SyncNowAsync(agentId, cancellationToken);
            return response.Dispatched ? Accepted(response) : Conflict(response);
        }
        catch (SensorValidationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}

using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Exceptions;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
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
    public sealed record SensorMuteRequest(int? DurationMinutes = null, string? TargetKey = null);

    private static async Task<IActionResult?> EnsureAgentAccessAsync(
        Guid agentId,
        ActionType action,
        IScopeContext scopeContext,
        IAgentRepository agentRepo,
        ISiteRepository siteRepo)
    {
        var agent = await agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return new NotFoundObjectResult(new { error = "Agente não encontrado." });

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Sensors, action);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return new ObjectResult(new { error = "Sem acesso a este agente." }) { StatusCode = StatusCodes.Status403Forbidden };

        return null;
    }

    [HttpGet]
    [RequirePermission(ResourceType.Sensors, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        Guid agentId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        CancellationToken cancellationToken)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.View, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var payload = await readService.GetForAgentAsync(agentId, cancellationToken);
        return payload is null ? NotFound(new { error = "Agente não encontrado." }) : Ok(payload);
    }

    [HttpGet("{sensorId:guid}/series")]
    [RequirePermission(ResourceType.Sensors, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetSeries(
        Guid agentId,
        Guid sensorId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? channels = null,
        [FromQuery] string? targetKey = null,
        CancellationToken cancellationToken = default)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.View, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

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
    [RequirePermission(ResourceType.Sensors, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetEvents(
        Guid agentId,
        Guid sensorId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        [FromQuery] string? targetKey = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.View, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var page = await seriesService.GetEventsAsync(agentId, sensorId, targetKey, cursor, limit, cancellationToken);
        return Ok(page);
    }

    [HttpPost("run")]
    [RequirePermission(ResourceType.Sensors, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> Run(
        Guid agentId,
        [FromBody] SensorRunRequest? request,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        CancellationToken cancellationToken)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.Execute, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

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
    [RequirePermission(ResourceType.Sensors, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> Reset(
        Guid agentId,
        Guid sensorId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        CancellationToken cancellationToken)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.Execute, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var count = await operationService.ResetAsync(agentId, sensorId, cancellationToken);
        return Ok(new { reset = count });
    }

    [HttpPost("{sensorId:guid}/mute")]
    [RequirePermission(ResourceType.Sensors, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> Mute(
        Guid agentId,
        Guid sensorId,
        [FromBody] SensorMuteRequest? request,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        CancellationToken cancellationToken)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.Execute, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var mutedUntil = await operationService.MuteAsync(
            agentId,
            sensorId,
            request?.DurationMinutes,
            request?.TargetKey,
            cancellationToken);
        return Ok(new { mutedUntil });
    }

    [HttpPost("sync-now")]
    [RequirePermission(ResourceType.Sensors, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> SyncNow(
        Guid agentId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        CancellationToken cancellationToken)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.Execute, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

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

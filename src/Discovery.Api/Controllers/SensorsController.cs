using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Exceptions;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    [RequirePermission(ResourceType.Sensors, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetSummary(
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        CancellationToken cancellationToken = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Sensors, ActionType.View);
        if (!access.HasGlobalAccess)
        {
            if (siteId.HasValue)
            {
                var site = await siteRepo.GetByIdAsync(siteId.Value);
                if (site is null)
                    return NotFound(new { error = "Site not found." });
                if (!TenantScopePolicy.Allows(access, site.ClientId, siteId.Value))
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este site." });
            }
            else if (clientId.HasValue)
            {
                if (!access.AllowedClientIds.Contains(clientId.Value))
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });
            }
            else if (access.AllowedClientIds.Count == 1 && access.AllowedSiteIds.Count == 0)
            {
                clientId = access.AllowedClientIds[0];
            }
            else if (access.AllowedSiteIds.Count == 1 && access.AllowedClientIds.Count == 0)
            {
                siteId = access.AllowedSiteIds[0];
            }
            else
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Resumo global de sensores requer acesso global." });
            }
        }

        var summary = await readService.GetFleetSummaryAsync(clientId, siteId, cancellationToken);
        return Ok(summary);
    }

    [HttpGet]
    [RequirePermission(ResourceType.Sensors, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAll(
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] DiscoveryDbContext db,
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
        var access = await scopeContext.GetAccessAsync(ResourceType.Sensors, ActionType.View);
        if (!access.HasGlobalAccess)
        {
            if (agentId.HasValue || siteId.HasValue || clientId.HasValue)
            {
                var explicitScope = agentId.HasValue ? AppApprovalScopeType.Agent
                    : siteId.HasValue ? AppApprovalScopeType.Site
                    : AppApprovalScopeType.Client;
                if (await EnsureSensorScopeAccessAsync(explicitScope, clientId, siteId, agentId, access, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
                    return denied;
            }
        }

        // Resposta aditiva: items/count continuam iguais; total/page/pageSize
        // entram para o cliente paginar de verdade (antes cortava em 500).
        var result = await definitions.ListAsync(
            scopeType, clientId, siteId, agentId, includeInactive,
            search, sensorType, isActive, page, pageSize, cancellationToken);

        if (!access.HasGlobalAccess)
        {
            var siteIds = result.Items.Where(i => i.SiteId.HasValue).Select(i => i.SiteId!.Value).Distinct().ToList();
            var agentIds = result.Items.Where(i => i.AgentId.HasValue).Select(i => i.AgentId!.Value).Distinct().ToList();
            var agentSiteMap = agentIds.Count == 0
                ? new Dictionary<Guid, Guid>()
                : await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.SiteId, cancellationToken);
            foreach (var sId in agentSiteMap.Values)
                if (!siteIds.Contains(sId)) siteIds.Add(sId);

            var siteClientMap = siteIds.Count == 0
                ? new Dictionary<Guid, Guid>()
                : await db.Sites.AsNoTracking().Where(s => siteIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.ClientId, cancellationToken);

            var filtered = result.Items.Where(i => i.ScopeType switch
            {
                AppApprovalScopeType.Global => true,
                AppApprovalScopeType.Client => i.ClientId.HasValue && access.AllowedClientIds.Contains(i.ClientId.Value),
                AppApprovalScopeType.Site => i.SiteId.HasValue && TenantScopePolicy.Allows(
                    access, i.ClientId ?? siteClientMap.GetValueOrDefault(i.SiteId.Value), i.SiteId.Value),
                AppApprovalScopeType.Agent => i.AgentId.HasValue && agentSiteMap.TryGetValue(i.AgentId.Value, out var aSiteId)
                    && TenantScopePolicy.Allows(access, siteClientMap.GetValueOrDefault(aSiteId), aSiteId),
                _ => false
            }).ToList();

            result.Items = filtered;
            result.Count = filtered.Count;
            result.Total = filtered.Count;
        }

        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Sensors, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        CancellationToken cancellationToken)
    {
        var dto = await definitions.GetAsync(id, cancellationToken);
        if (dto is null)
            return NotFound(new { error = "Sensor não encontrado." });

        var access = await scopeContext.GetAccessAsync(ResourceType.Sensors, ActionType.View);
        if (await EnsureSensorScopeAccessAsync(dto.ScopeType, dto.ClientId, dto.SiteId, dto.AgentId, access, siteRepo, agentRepo, allowGlobalRead: true) is { } denied)
            return denied;

        return Ok(dto);
    }

    [HttpPost]
    [RequirePermission(ResourceType.Sensors, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> Create(
        [FromBody] SensorUpsertRequest request,
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        CancellationToken cancellationToken)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Sensors, ActionType.Create);
        if (await EnsureSensorScopeAccessAsync(request.ScopeType, request.ClientId, request.SiteId, request.AgentId, access, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
            return denied;

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
    [RequirePermission(ResourceType.Sensors, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] SensorUpsertRequest request,
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        CancellationToken cancellationToken)
    {
        var existing = await definitions.GetAsync(id, cancellationToken);
        if (existing is null)
            return NotFound(new { error = "Sensor não encontrado." });

        var access = await scopeContext.GetAccessAsync(ResourceType.Sensors, ActionType.Edit);
        if (await EnsureSensorScopeAccessAsync(existing.ScopeType, existing.ClientId, existing.SiteId, existing.AgentId, access, siteRepo, agentRepo, allowGlobalRead: false) is { } deniedExisting)
            return deniedExisting;
        if (await EnsureSensorScopeAccessAsync(request.ScopeType, request.ClientId, request.SiteId, request.AgentId, access, siteRepo, agentRepo, allowGlobalRead: false) is { } deniedTarget)
            return deniedTarget;

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
    [RequirePermission(ResourceType.Sensors, ActionType.Delete, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        CancellationToken cancellationToken)
    {
        var existing = await definitions.GetAsync(id, cancellationToken);
        if (existing is null)
            return NotFound(new { error = "Sensor não encontrado." });

        var access = await scopeContext.GetAccessAsync(ResourceType.Sensors, ActionType.Delete);
        if (await EnsureSensorScopeAccessAsync(existing.ScopeType, existing.ClientId, existing.SiteId, existing.AgentId, access, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
            return denied;

        var deleted = await definitions.DeleteAsync(id, CurrentActor(), cancellationToken);
        return deleted ? NoContent() : NotFound(new { error = "Sensor não encontrado." });
    }

    public sealed record SensorPinRequest(bool IsPinned);

    [HttpPost("{id:guid}/pin")]
    [RequirePermission(ResourceType.Sensors, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> SetPinned(
        Guid id,
        [FromBody] SensorPinRequest request,
        [FromServices] IScopeContext scopeContext,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        CancellationToken cancellationToken)
    {
        var existing = await definitions.GetAsync(id, cancellationToken);
        if (existing is null)
            return NotFound(new { error = "Sensor não encontrado." });

        var access = await scopeContext.GetAccessAsync(ResourceType.Sensors, ActionType.Edit);
        if (await EnsureSensorScopeAccessAsync(existing.ScopeType, existing.ClientId, existing.SiteId, existing.AgentId, access, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
            return denied;

        var updated = await definitions.SetPinnedAsync(id, request.IsPinned, cancellationToken);
        return updated ? Ok(new { id, isPinned = request.IsPinned }) : NotFound(new { error = "Sensor não encontrado." });
    }

    private async Task<IActionResult?> EnsureSensorScopeAccessAsync(
        AppApprovalScopeType scopeType,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        UserScopeAccess access,
        ISiteRepository siteRepo,
        IAgentRepository agentRepo,
        bool allowGlobalRead)
    {
        if (access.HasGlobalAccess)
            return null;

        switch (scopeType)
        {
            case AppApprovalScopeType.Global:
                return allowGlobalRead
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Apenas administradores globais podem alterar sensores globais." });

            case AppApprovalScopeType.Client:
                return clientId.HasValue && access.AllowedClientIds.Contains(clientId.Value)
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });

            case AppApprovalScopeType.Site:
                if (!siteId.HasValue)
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este site." });
                var site = await siteRepo.GetByIdAsync(siteId.Value);
                if (site is null)
                    return NotFound(new { error = "Site not found." });
                return TenantScopePolicy.Allows(access, site.ClientId, siteId.Value)
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este site." });

            case AppApprovalScopeType.Agent:
                if (!agentId.HasValue)
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este agente." });
                var agent = await agentRepo.GetByIdAsync(agentId.Value);
                if (agent is null)
                    return NotFound(new { error = "Agent not found." });
                var agentSite = await siteRepo.GetByIdAsync(agent.SiteId);
                return TenantScopePolicy.Allows(access, agentSite?.ClientId ?? Guid.Empty, agent.SiteId)
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este agente." });

            default:
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Escopo não autorizado." });
        }
    }

    private string CurrentActor()
        => HttpContext.Items["UserId"] is Guid userId
            ? $"user:{userId}"
            : HttpContext.Items["Username"] as string ?? "system";
}

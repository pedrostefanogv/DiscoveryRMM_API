using Discovery.Api;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints de patches no escopo do agente (aba "Atualizações" do detalhe).
/// A autorização combina a permissão do recurso Patches com a access list de
/// agentes (o usuário não pode ler/agir em agente fora do seu escopo).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/agents/{agentId:guid}/windows-updates")]
[RequirePermission(ResourceType.Patches, ActionType.View)]
public sealed class AgentPatchesController(
    IMediator mediator,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IScopeContext scopeContext) : ControllerBase
{
    public sealed record InstallRequest(bool Force = false, string? PostAction = null, string? Reason = null);
    public sealed record InstallApprovedRequest(string? PostAction = null, bool Confirm = false);
    public sealed record SetApprovalRequest(
        PatchApprovalScopeType Scope = PatchApprovalScopeType.Agent,
        PatchApprovalActionType Action = PatchApprovalActionType.Approve,
        string? Reason = null,
        Guid? TargetScopeId = null);
    public sealed record BulkRequest(
        string Action,
        IReadOnlyList<string> UpdateKeys,
        PatchApprovalScopeType? Scope = null,
        string? Reason = null);
    public sealed record ScanRequest(string? Reason = null);
    public sealed record OsPolicyRequest(bool HideDenied = true);

    [HttpGet("page")]
    public async Task<IActionResult> GetPage(
        Guid agentId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? severity = null,
        [FromQuery] string order = "desc",
        CancellationToken ct = default)
    {
        var forbidden = await EnsureAgentAccessAsync(agentId, ActionType.View);
        if (forbidden is not null) return forbidden;

        var query = new GetAgentWindowsUpdatesPageQuery(
            agentId, page, pageSize, search, status, severity,
            !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase));
        return (await mediator.Send(query, ct)).ToActionResult();
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot(Guid agentId, CancellationToken ct = default)
    {
        var forbidden = await EnsureAgentAccessAsync(agentId, ActionType.View);
        if (forbidden is not null) return forbidden;

        return (await mediator.Send(new GetAgentWindowsUpdatesSnapshotQuery(agentId), ct)).ToActionResult();
    }

    [HttpPost("scan")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute)]
    public async Task<IActionResult> Scan(Guid agentId, [FromBody] ScanRequest? request = null, CancellationToken ct = default)
    {
        var forbidden = await EnsureAgentAccessAsync(agentId, ActionType.Execute);
        if (forbidden is not null) return forbidden;

        var result = await mediator.Send(
            new ScanAgentWindowsUpdatesCommand(agentId, CorrelationId()), ct);
        return AcceptedOrError(result);
    }

    [HttpPost("{updateKey}/install")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute)]
    public async Task<IActionResult> Install(
        Guid agentId,
        string updateKey,
        [FromBody] InstallRequest? request = null,
        CancellationToken ct = default)
    {
        var forbidden = await EnsureAgentAccessAsync(agentId, ActionType.Execute);
        if (forbidden is not null) return forbidden;

        var result = await mediator.Send(new InstallAgentWindowsUpdateCommand(
            agentId,
            updateKey,
            request?.Force ?? false,
            request?.PostAction,
            CorrelationId(),
            CurrentUserId(),
            RemoteIp(),
            request?.Reason), ct);

        return result.Match<IActionResult>(
            success: commandId => Accepted(new { success = true, dispatched = true, commandId }),
            failure: errors => errors[0].Code switch
            {
                "NotFound" => NotFound(new { error = errors[0].Message }),
                "Conflict" => Conflict(new { requiresConfirmation = true, error = errors[0].Message }),
                "Validation" => BadRequest(new { error = errors[0].Message }),
                _ => BadRequest(new { error = errors[0].Message })
            });
    }

    [HttpPost("install-approved")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute)]
    public async Task<IActionResult> InstallApproved(
        Guid agentId,
        [FromBody] InstallApprovedRequest? request = null,
        CancellationToken ct = default)
    {
        var forbidden = await EnsureAgentAccessAsync(agentId, ActionType.Execute);
        if (forbidden is not null) return forbidden;

        var result = await mediator.Send(new InstallAgentApprovedWindowsUpdatesCommand(
            agentId,
            request?.PostAction,
            request?.Confirm ?? false,
            CorrelationId()), ct);
        return AcceptedOrError(result);
    }

    [HttpPost("{updateKey}/approve")]
    [RequirePermission(ResourceType.Patches, ActionType.Edit)]
    public Task<IActionResult> Approve(
        Guid agentId,
        string updateKey,
        [FromBody] SetApprovalRequest? request = null,
        CancellationToken ct = default)
        => SetApproval(agentId, updateKey, request, PatchApprovalActionType.Approve, ct);

    [HttpPost("{updateKey}/deny")]
    [RequirePermission(ResourceType.Patches, ActionType.Edit)]
    public Task<IActionResult> Deny(
        Guid agentId,
        string updateKey,
        [FromBody] SetApprovalRequest? request = null,
        CancellationToken ct = default)
        => SetApproval(agentId, updateKey, request, PatchApprovalActionType.Deny, ct);

    private async Task<IActionResult> SetApproval(
        Guid agentId,
        string updateKey,
        SetApprovalRequest? request,
        PatchApprovalActionType action,
        CancellationToken ct)
    {
        var forbidden = await EnsureAgentAccessAsync(agentId, ActionType.Edit);
        if (forbidden is not null) return forbidden;

        var result = await mediator.Send(new SetAgentWindowsUpdateApprovalCommand(
            agentId,
            updateKey,
            request?.Scope ?? PatchApprovalScopeType.Agent,
            action,
            request?.Reason,
            CurrentUserId(),
            RemoteIp(),
            request?.TargetScopeId), ct);

        return result.Match<IActionResult>(
            success: dto => Ok(dto),
            failure: errors => errors[0].Code switch
            {
                "NotFound" => NotFound(new { error = errors[0].Message }),
                "Validation" => BadRequest(new { error = errors[0].Message }),
                _ => BadRequest(new { error = errors[0].Message })
            });
    }

    [HttpPost("os-policy/apply")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute)]
    public async Task<IActionResult> ApplyOsPolicy(
        Guid agentId,
        [FromBody] OsPolicyRequest? request = null,
        CancellationToken ct = default)
    {
        var forbidden = await EnsureAgentAccessAsync(agentId, ActionType.Execute);
        if (forbidden is not null) return forbidden;

        var result = await mediator.Send(new ApplyAgentPatchOsPolicyCommand(
            agentId,
            request?.HideDenied ?? true,
            CurrentUserId(),
            RemoteIp()), ct);
        return AcceptedOrError(result);
    }

    [HttpPost("os-policy/rollback")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute)]
    public async Task<IActionResult> RollbackOsPolicy(Guid agentId, CancellationToken ct = default)
    {
        var forbidden = await EnsureAgentAccessAsync(agentId, ActionType.Execute);
        if (forbidden is not null) return forbidden;

        var result = await mediator.Send(new RollbackAgentPatchOsPolicyCommand(
            agentId, CurrentUserId()), ct);
        return AcceptedOrError(result);
    }

    [HttpPost("bulk")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute)]
    public async Task<IActionResult> Bulk(
        Guid agentId,
        [FromBody] BulkRequest request,
        CancellationToken ct = default)
    {
        var forbidden = await EnsureAgentAccessAsync(agentId, ActionType.Execute);
        if (forbidden is not null) return forbidden;

        if (request.UpdateKeys is null || request.UpdateKeys.Count == 0)
            return BadRequest(new { error = "Informe ao menos uma atualização." });

        var result = await mediator.Send(new BulkAgentWindowsUpdateCommand(
            agentId,
            request.Action,
            request.UpdateKeys,
            request.Scope,
            request.Reason,
            CurrentUserId(),
            RemoteIp()), ct);
        return AcceptedOrError(result);
    }

    private async Task<IActionResult?> EnsureAgentAccessAsync(Guid agentId, ActionType action)
    {
        var agent = await agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, action);
        if (access.HasGlobalAccess || access.AllowedClientIds.Contains(site.ClientId))
            return null;

        return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este agente." });
    }

    private IActionResult AcceptedOrError(Discovery.Core.Cqrs.Result<PatchDispatchDto> result)
        => result.Match<IActionResult>(
            success: dto => Accepted(new { success = true, dispatched = dto.Dispatched, skipped = dto.Skipped, commandIds = dto.CommandIds, message = dto.Message }),
            failure: errors => errors[0].Code switch
            {
                "NotFound" => NotFound(new { error = errors[0].Message }),
                "Conflict" => Conflict(new { requiresConfirmation = true, error = errors[0].Message }),
                "Validation" => BadRequest(new { error = errors[0].Message }),
                _ => BadRequest(new { error = errors[0].Message })
            });

    private string? CorrelationId()
        => Request.Headers.TryGetValue("X-Correlation-Id", out var values) ? values.FirstOrDefault() : null;

    private string? CurrentUserId()
        => HttpContext.Items["UserId"] is Guid userId ? $"user:{userId}" : null;

    private string? RemoteIp()
        => HttpContext.Connection.RemoteIpAddress?.ToString();
}

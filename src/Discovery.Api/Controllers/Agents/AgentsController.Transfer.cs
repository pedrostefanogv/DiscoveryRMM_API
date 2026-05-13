using Discovery.Api.Filters;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints de transferência de agentes entre sites/clientes.
/// </summary>
public partial class AgentsController
{
    [HttpPost("{agentId:guid}/transfer")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit, ScopeSource.FromRoute)]
    public async Task<IActionResult> TransferAgent(
        Guid agentId,
        [FromBody] TransferAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        try
        {
            var result = await _agentTransferService.TransferAsync(
                agentId,
                request.TargetSiteId,
                userId,
                request.Reason,
                cancellationToken);

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
    }

    [HttpPost("transfer/bulk")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> BulkTransferAgents(
        [FromBody] BulkTransferAgentsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        if (request.AgentIds is null || request.AgentIds.Count == 0)
            return BadRequest(new { error = "At least one agent ID is required." });

        if (request.AgentIds.Count > 100)
            return BadRequest(new { error = "Maximum of 100 agents per bulk transfer." });

        var result = await _agentTransferService.BulkTransferAsync(
            request.AgentIds,
            request.TargetSiteId,
            userId,
            request.Reason,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("{agentId:guid}/validate-transfer")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> ValidateTransfer(
        Guid agentId,
        [FromQuery] Guid targetSiteId,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var validation = await _agentTransferService.ValidateAsync(
            agentId,
            targetSiteId,
            userId,
            cancellationToken);

        return Ok(validation);
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────

public record TransferAgentRequest(Guid TargetSiteId, string? Reason);
public record BulkTransferAgentsRequest(IReadOnlyList<Guid> AgentIds, Guid TargetSiteId, string? Reason);

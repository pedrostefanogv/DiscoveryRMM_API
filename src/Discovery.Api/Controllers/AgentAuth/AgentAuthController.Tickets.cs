using Discovery.Api.Services;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent ticket endpoints: CRUD, comments, workflow state transitions, close/rate.
/// </summary>
public partial class AgentAuthController
{
    [HttpGet("me/tickets")]
    public async Task<IActionResult> GetMyTickets([FromQuery] Guid? workflowStateId)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
            return blocked;

        var tickets = await _ticketRepo.GetByAgentIdAsync(agentId, workflowStateId);

        var ticketsWithState = new List<object>();
        foreach (var ticket in tickets)
        {
            var state = await _workflowRepo.GetStateByIdAsync(ticket.WorkflowStateId);
            ticketsWithState.Add(new
            {
                ticket.Id, ticket.ClientId, ticket.SiteId, ticket.AgentId,
                ticket.DepartmentId, ticket.WorkflowProfileId, ticket.Title,
                ticket.Description, ticket.Category, ticket.WorkflowStateId,
                ticket.Priority, ticket.AssignedToUserId,
                ticket.SlaExpiresAt, ticket.SlaBreached,
                ticket.Rating, ticket.RatedAt, ticket.RatedBy,
                ticket.CreatedAt, ticket.UpdatedAt, ticket.ClosedAt, ticket.DaysOpen,
                WorkflowState = state is null ? null : new { state.Id, state.Name, state.Color, state.IsInitial, state.IsFinal, state.SortOrder }
            });
        }

        return Ok(ticketsWithState);
    }

    [HttpGet("me/tickets/{ticketId:guid}")]
    public async Task<IActionResult> GetMyTicket(Guid ticketId)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound(new { error = "Ticket not found." });
        if (ticket.AgentId != agentId) return StatusCode(403, new { error = "Ticket does not belong to this agent." });

        var state = await _workflowRepo.GetStateByIdAsync(ticket.WorkflowStateId);
        return Ok(new
        {
            ticket.Id, ticket.ClientId, ticket.SiteId, ticket.AgentId,
            ticket.DepartmentId, ticket.WorkflowProfileId, ticket.Title,
            ticket.Description, ticket.Category, ticket.WorkflowStateId,
            ticket.Priority, ticket.AssignedToUserId,
            ticket.SlaExpiresAt, ticket.SlaBreached,
            ticket.Rating, ticket.RatedAt, ticket.RatedBy,
            ticket.CreatedAt, ticket.UpdatedAt, ticket.ClosedAt, ticket.DaysOpen,
            WorkflowState = state is null ? null : new { state.Id, state.Name, state.Color, state.IsInitial, state.IsFinal, state.SortOrder }
        });
    }

    [HttpPost("me/tickets")]
    public async Task<IActionResult> CreateMyTicket([FromBody] AgentCreateTicketRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var site = await _siteRepo.GetByIdAsync(agent!.SiteId);
        if (site is null) return BadRequest(new { error = "Site not found for this agent." });

        var initialState = await _workflowRepo.GetInitialStateAsync(site.ClientId);
        if (initialState is null) return BadRequest(new { error = "No initial workflow state configured for this client." });

        WorkflowProfile? workflowProfile = null;
        DateTime? slaExpiresAt = null;

        if (request.WorkflowProfileId.HasValue)
        {
            workflowProfile = await _workflowProfileRepo.GetByIdAsync(request.WorkflowProfileId.Value);
            if (workflowProfile is null) return BadRequest(new { error = "Workflow profile not found." });
        }
        else if (request.DepartmentId.HasValue)
        {
            workflowProfile = await _workflowProfileRepo.GetDefaultByDepartmentAsync(request.DepartmentId.Value);
        }

        if (workflowProfile != null)
            slaExpiresAt = await _slaService.CalculateSlaExpiryAsync(workflowProfile.Id, DateTime.UtcNow);

        var ticket = new Ticket
        {
            ClientId = site.ClientId,
            SiteId = agent.SiteId,
            AgentId = agentId,
            DepartmentId = request.DepartmentId,
            WorkflowProfileId = workflowProfile?.Id,
            Title = request.Title,
            Description = request.Description ?? string.Empty,
            Priority = request.Priority ?? (workflowProfile?.DefaultPriority ?? TicketPriority.Medium),
            Category = request.Category,
            WorkflowStateId = initialState.Id,
            SlaExpiresAt = slaExpiresAt
        };

        var created = await _ticketRepo.CreateAsync(ticket);
        await _activityLogService.LogActivityAsync(created.Id, TicketActivityType.Created, null,
            $"Agent {agent.Hostname}", initialState.Id.ToString(), "Ticket created by agent");

        return CreatedAtAction(nameof(GetMyTicket), new { ticketId = created.Id }, created);
    }

    [HttpPost("me/tickets/{ticketId:guid}/comments")]
    public async Task<IActionResult> AddMyTicketComment(Guid ticketId, [FromBody] AgentAddCommentRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound(new { error = "Ticket not found." });
        if (ticket.AgentId != agentId) return StatusCode(403, new { error = "Ticket does not belong to this agent." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        var comment = new TicketComment
        {
            TicketId = ticketId,
            Author = $"Agent: {agent?.Hostname ?? agentId.ToString()}",
            Content = request.Content,
            IsInternal = request.IsInternal ?? false
        };

        var created = await _ticketRepo.AddCommentAsync(comment);
        await _activityLogService.LogActivityAsync(ticketId, TicketActivityType.Commented, null, null, null,
            $"Comment added by {created.Author}");

        return Created($"api/agent-auth/me/tickets/{ticketId}/comments", created);
    }

    [HttpGet("me/tickets/{ticketId:guid}/comments")]
    public async Task<IActionResult> GetMyTicketComments(Guid ticketId)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound(new { error = "Ticket not found." });
        if (ticket.AgentId != agentId) return StatusCode(403, new { error = "Ticket does not belong to this agent." });

        return Ok(await _ticketRepo.GetCommentsAsync(ticketId));
    }

    [HttpPatch("me/tickets/{ticketId:guid}/workflow-state")]
    public async Task<IActionResult> UpdateMyTicketWorkflowState(Guid ticketId, [FromBody] AgentUpdateWorkflowStateRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound(new { error = "Ticket not found." });
        if (ticket.AgentId != agentId) return StatusCode(403, new { error = "Ticket does not belong to this agent." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        var valid = await _workflowRepo.IsTransitionValidAsync(ticket.WorkflowStateId, request.WorkflowStateId, ticket.ClientId);
        if (!valid) return BadRequest(new { error = "Invalid workflow transition." });

        var oldStateId = ticket.WorkflowStateId;
        var newState = await _workflowRepo.GetStateByIdAsync(request.WorkflowStateId);
        DateTime? closedAt = newState?.IsFinal == true ? DateTime.UtcNow : null;

        await _ticketRepo.UpdateWorkflowStateAsync(ticketId, request.WorkflowStateId, closedAt);
        await _activityLogService.LogActivityAsync(ticketId, TicketActivityType.StateChanged, null,
            oldStateId.ToString(), request.WorkflowStateId.ToString(),
            $"Changed by agent {agent?.Hostname ?? agentId.ToString()}");

        return Ok(new { message = "Workflow state updated", ticket = await _ticketRepo.GetByIdAsync(ticketId) });
    }

    [HttpPost("me/tickets/{ticketId:guid}/close")]
    public async Task<IActionResult> CloseAndRateTicket(Guid ticketId, [FromBody] AgentCloseTicketRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound(new { error = "Ticket not found." });
        if (ticket.AgentId != agentId) return StatusCode(403, new { error = "Ticket does not belong to this agent." });

        var agent = await _agentRepo.GetByIdAsync(agentId);

        if (request.Rating.HasValue && (request.Rating.Value < 0 || request.Rating.Value > 5))
            return BadRequest(new { error = "Rating must be between 0 and 5." });

        Guid targetStateId;
        if (request.WorkflowStateId.HasValue)
        {
            var targetState = await _workflowRepo.GetStateByIdAsync(request.WorkflowStateId.Value);
            if (targetState is null) return BadRequest(new { error = "Workflow state not found." });
            if (!targetState.IsFinal) return BadRequest(new { error = "Specified workflow state is not a final state." });
            targetStateId = request.WorkflowStateId.Value;
        }
        else
        {
            var finalStates = await _workflowRepo.GetStatesAsync(ticket.ClientId);
            var closedState = finalStates.FirstOrDefault(s => s.IsFinal && s.Name.Contains("Closed", StringComparison.OrdinalIgnoreCase))
                           ?? finalStates.FirstOrDefault(s => s.IsFinal);
            if (closedState is null) return BadRequest(new { error = "No final workflow state available." });
            targetStateId = closedState.Id;
        }

        var valid = await _workflowRepo.IsTransitionValidAsync(ticket.WorkflowStateId, targetStateId, ticket.ClientId);
        if (!valid) return BadRequest(new { error = "Invalid workflow transition to close ticket." });

        var oldStateId = ticket.WorkflowStateId;
        ticket.WorkflowStateId = targetStateId;
        ticket.ClosedAt = DateTime.UtcNow;

        if (request.Rating.HasValue)
        {
            ticket.Rating = request.Rating.Value;
            ticket.RatedAt = DateTime.UtcNow;
            ticket.RatedBy = $"Agent: {agent?.Hostname ?? agentId.ToString()}";
        }

        await _ticketRepo.UpdateAsync(ticket);
        await _activityLogService.LogActivityAsync(ticketId, TicketActivityType.StateChanged, null,
            oldStateId.ToString(), targetStateId.ToString(),
            request.Rating.HasValue
                ? $"Ticket closed by agent {agent?.Hostname ?? agentId.ToString()} with rating {request.Rating.Value}/5"
                : $"Ticket closed by agent {agent?.Hostname ?? agentId.ToString()}");

        if (!string.IsNullOrWhiteSpace(request.Comment))
        {
            var comment = new TicketComment { TicketId = ticketId, Author = $"Agent: {agent?.Hostname ?? agentId.ToString()}", Content = request.Comment, IsInternal = false };
            var createdComment = await _ticketRepo.AddCommentAsync(comment);
            await _activityLogService.LogActivityAsync(ticketId, TicketActivityType.Commented, null, null, null, $"Comment added by {createdComment.Author}");
        }

        return Ok(new { message = "Ticket closed successfully", ticket = await _ticketRepo.GetByIdAsync(ticketId), rating = request.Rating });
    }
}

// ── Request DTOs (used only by the agent ticket endpoints) ──────────────────
public sealed record AgentAddCommentRequest(string Content, bool? IsInternal);
public sealed record AgentUpdateWorkflowStateRequest(Guid WorkflowStateId);
public sealed record AgentCloseTicketRequest(int? Rating, Guid? WorkflowStateId, string? Comment);

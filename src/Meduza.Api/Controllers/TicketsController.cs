using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TicketsController : ControllerBase
{
    private readonly ITicketRepository _repo;
    private readonly IWorkflowRepository _workflowRepo;

    public TicketsController(ITicketRepository repo, IWorkflowRepository workflowRepo)
    {
        _repo = repo;
        _workflowRepo = workflowRepo;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? workflowStateId, [FromQuery] int limit = 100, [FromQuery] int offset = 0)
    {
        var tickets = await _repo.GetAllAsync(workflowStateId, limit, offset);
        return Ok(tickets);
    }

    [HttpGet("by-client/{clientId:guid}")]
    public async Task<IActionResult> GetByClient(Guid clientId, [FromQuery] Guid? workflowStateId)
    {
        var tickets = await _repo.GetByClientIdAsync(clientId, workflowStateId);
        return Ok(tickets);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var ticket = await _repo.GetByIdAsync(id);
        return ticket is null ? NotFound() : Ok(ticket);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTicketRequest request)
    {
        // Buscar estado inicial do workflow (global ou do client)
        var initialState = await _workflowRepo.GetInitialStateAsync(request.ClientId);
        if (initialState is null)
            return BadRequest("No initial workflow state configured.");

        var ticket = new Ticket
        {
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            AgentId = request.AgentId,
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority,
            Category = request.Category,
            WorkflowStateId = initialState.Id
        };
        var created = await _repo.CreateAsync(ticket);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        ticket.Title = request.Title;
        ticket.Description = request.Description;
        ticket.Priority = request.Priority;
        ticket.AssignedTo = request.AssignedTo;
        ticket.Category = request.Category;

        await _repo.UpdateAsync(ticket);
        return Ok(ticket);
    }

    [HttpPatch("{id:guid}/workflow-state")]
    public async Task<IActionResult> UpdateWorkflowState(Guid id, [FromBody] UpdateWorkflowStateRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        // Validar se a transição é permitida
        var valid = await _workflowRepo.IsTransitionValidAsync(ticket.WorkflowStateId, request.WorkflowStateId, ticket.ClientId);
        if (!valid) return BadRequest("Invalid workflow transition.");

        // Verificar se o novo estado é final (para setar ClosedAt)
        var newState = await _workflowRepo.GetStateByIdAsync(request.WorkflowStateId);
        if (newState?.IsFinal == true)
            ticket.ClosedAt = DateTime.UtcNow;

        await _repo.UpdateWorkflowStateAsync(id, request.WorkflowStateId);
        return Ok();
    }

    // --- Comments ---

    [HttpGet("{id:guid}/comments")]
    public async Task<IActionResult> GetComments(Guid id)
    {
        var comments = await _repo.GetCommentsAsync(id);
        return Ok(comments);
    }

    [HttpPost("{id:guid}/comments")]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddCommentRequest request)
    {
        var ticket = await _repo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var comment = new TicketComment
        {
            TicketId = id,
            Author = request.Author,
            Content = request.Content,
            IsInternal = request.IsInternal
        };
        var created = await _repo.AddCommentAsync(comment);
        return Created($"api/tickets/{id}/comments", created);
    }
}

public record CreateTicketRequest(Guid ClientId, Guid? SiteId, Guid? AgentId, string Title, string Description, TicketPriority Priority, string? Category);
public record UpdateTicketRequest(string Title, string Description, TicketPriority Priority, string? AssignedTo, string? Category);
public record UpdateWorkflowStateRequest(Guid WorkflowStateId);
public record AddCommentRequest(string Author, string Content, bool IsInternal = false);

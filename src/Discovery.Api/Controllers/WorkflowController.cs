using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorkflowController : ControllerBase
{
    private readonly IWorkflowRepository _repo;

    public WorkflowController(IWorkflowRepository repo) => _repo = repo;

    // --- States ---

    [HttpGet("states")]
    public async Task<IActionResult> GetStates([FromQuery] Guid? clientId)
    {
        var states = await _repo.GetStatesAsync(clientId);
        return Ok(states);
    }

    [HttpGet("states/{id:guid}")]
    public async Task<IActionResult> GetState(Guid id)
    {
        var state = await _repo.GetStateByIdAsync(id);
        return state is null ? NotFound() : Ok(state);
    }

    [HttpPost("states")]
    public async Task<IActionResult> CreateState([FromBody] CreateWorkflowStateRequest request)
    {
        var state = new WorkflowState
        {
            ClientId = request.ClientId,
            Name = request.Name,
            Color = request.Color,
            IsInitial = request.IsInitial,
            IsFinal = request.IsFinal,
            SortOrder = request.SortOrder
        };
        var created = await _repo.CreateStateAsync(state);
        return CreatedAtAction(nameof(GetState), new { id = created.Id }, created);
    }

    [HttpPut("states/{id:guid}")]
    public async Task<IActionResult> UpdateState(Guid id, [FromBody] UpdateStateRequest request)
    {
        var state = await _repo.GetStateByIdAsync(id);
        if (state is null) return NotFound();

        state.Name = request.Name;
        state.Color = request.Color;
        state.IsInitial = request.IsInitial;
        state.IsFinal = request.IsFinal;
        state.SortOrder = request.SortOrder;

        await _repo.UpdateStateAsync(state);
        return Ok(state);
    }

    [HttpDelete("states/{id:guid}")]
    public async Task<IActionResult> DeleteState(Guid id)
    {
        await _repo.DeleteStateAsync(id);
        return NoContent();
    }

    // --- Transitions ---

    [HttpGet("transitions")]
    public async Task<IActionResult> GetTransitions([FromQuery] Guid? clientId)
    {
        var transitions = await _repo.GetTransitionsAsync(clientId);
        return Ok(transitions);
    }

    [HttpGet("transitions/from/{fromStateId:guid}")]
    public async Task<IActionResult> GetTransitionsFromState(Guid fromStateId, [FromQuery] Guid? clientId)
    {
        var transitions = await _repo.GetTransitionsFromStateAsync(fromStateId, clientId);
        return Ok(transitions);
    }

    [HttpPost("transitions")]
    public async Task<IActionResult> CreateTransition([FromBody] CreateWorkflowTransitionRequest request)
    {
        var transition = new WorkflowTransition
        {
            ClientId = request.ClientId,
            FromStateId = request.FromStateId,
            ToStateId = request.ToStateId,
            Name = request.Name
        };
        var created = await _repo.CreateTransitionAsync(transition);
        return Created($"api/workflow/transitions", created);
    }

    [HttpDelete("transitions/{id:guid}")]
    public async Task<IActionResult> DeleteTransition(Guid id)
    {
        await _repo.DeleteTransitionAsync(id);
        return NoContent();
    }
}

public record CreateWorkflowStateRequest(Guid? ClientId, string Name, string? Color, bool IsInitial, bool IsFinal, int SortOrder);
public record UpdateStateRequest(string Name, string? Color, bool IsInitial, bool IsFinal, int SortOrder);
public record CreateWorkflowTransitionRequest(Guid? ClientId, Guid FromStateId, Guid ToStateId, string Name);

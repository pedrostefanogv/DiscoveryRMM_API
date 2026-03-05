using Meduza.Core.Entities;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api")]
public class NotesController : ControllerBase
{
    private readonly IEntityNoteRepository _notes;
    private readonly IClientRepository _clients;
    private readonly ISiteRepository _sites;
    private readonly IAgentRepository _agents;

    public NotesController(
        IEntityNoteRepository notes,
        IClientRepository clients,
        ISiteRepository sites,
        IAgentRepository agents)
    {
        _notes = notes;
        _clients = clients;
        _sites = sites;
        _agents = agents;
    }

    // ============ Client notes ============

    [HttpGet("clients/{clientId:guid}/notes")]
    public async Task<IActionResult> GetClientNotes(Guid clientId)
    {
        var client = await _clients.GetByIdAsync(clientId);
        if (client is null) return NotFound(new { error = "Client not found." });

        var notes = await _notes.GetByClientIdAsync(clientId);
        return Ok(notes);
    }

    [HttpPost("clients/{clientId:guid}/notes")]
    public async Task<IActionResult> CreateClientNote(Guid clientId, [FromBody] CreateNoteRequest request)
    {
        var client = await _clients.GetByIdAsync(clientId);
        if (client is null) return NotFound(new { error = "Client not found." });

        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Content is required." });

        var note = new EntityNote
        {
            ClientId = clientId,
            Content = request.Content.Trim(),
            Author = request.Author,
            IsPinned = request.IsPinned
        };

        var created = await _notes.CreateAsync(note);
        return Created($"/api/notes/{created.Id}", created);
    }

    // ============ Site notes ============

    [HttpGet("sites/{siteId:guid}/notes")]
    public async Task<IActionResult> GetSiteNotes(Guid siteId)
    {
        var site = await _sites.GetByIdAsync(siteId);
        if (site is null) return NotFound(new { error = "Site not found." });

        var notes = await _notes.GetBySiteIdAsync(siteId);
        return Ok(notes);
    }

    [HttpPost("sites/{siteId:guid}/notes")]
    public async Task<IActionResult> CreateSiteNote(Guid siteId, [FromBody] CreateNoteRequest request)
    {
        var site = await _sites.GetByIdAsync(siteId);
        if (site is null) return NotFound(new { error = "Site not found." });

        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Content is required." });

        var note = new EntityNote
        {
            SiteId = siteId,
            Content = request.Content.Trim(),
            Author = request.Author,
            IsPinned = request.IsPinned
        };

        var created = await _notes.CreateAsync(note);
        return Created($"/api/notes/{created.Id}", created);
    }

    // ============ Agent notes ============

    [HttpGet("agents/{agentId:guid}/notes")]
    public async Task<IActionResult> GetAgentNotes(Guid agentId)
    {
        var agent = await _agents.GetByIdAsync(agentId);
        if (agent is null) return NotFound(new { error = "Agent not found." });

        var notes = await _notes.GetByAgentIdAsync(agentId);
        return Ok(notes);
    }

    [HttpPost("agents/{agentId:guid}/notes")]
    public async Task<IActionResult> CreateAgentNote(Guid agentId, [FromBody] CreateNoteRequest request)
    {
        var agent = await _agents.GetByIdAsync(agentId);
        if (agent is null) return NotFound(new { error = "Agent not found." });

        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Content is required." });

        var note = new EntityNote
        {
            AgentId = agentId,
            Content = request.Content.Trim(),
            Author = request.Author,
            IsPinned = request.IsPinned
        };

        var created = await _notes.CreateAsync(note);
        return Created($"/api/notes/{created.Id}", created);
    }

    // ============ Shared note operations ============

    [HttpGet("notes/{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var note = await _notes.GetByIdAsync(id);
        return note is null ? NotFound() : Ok(note);
    }

    [HttpPut("notes/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateNoteRequest request)
    {
        var note = await _notes.GetByIdAsync(id);
        if (note is null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Content is required." });

        note.Content = request.Content.Trim();
        note.Author = request.Author;
        note.IsPinned = request.IsPinned;

        await _notes.UpdateAsync(note);
        return Ok(note);
    }

    [HttpDelete("notes/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var note = await _notes.GetByIdAsync(id);
        if (note is null) return NotFound();

        await _notes.DeleteAsync(id);
        return NoContent();
    }
}

public record CreateNoteRequest(string Content, string? Author, bool IsPinned = false);
public record UpdateNoteRequest(string Content, string? Author, bool IsPinned = false);

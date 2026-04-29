using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Sessões de suporte remoto MeshCentral vinculadas a um ticket.
/// Permite registrar o início/fim de uma sessão remota e consultá-las no histórico do ticket.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/tickets/{ticketId:guid}/remote-sessions")]
public class TicketRemoteSessionsController : ControllerBase
{
    private readonly ITicketRepository _ticketRepo;
    private readonly ITicketRemoteSessionRepository _sessionRepo;
    private readonly IAgentRepository _agentRepo;
    private readonly IActivityLogService _activityLogService;

    public TicketRemoteSessionsController(
        ITicketRepository ticketRepo,
        ITicketRemoteSessionRepository sessionRepo,
        IAgentRepository agentRepo,
        IActivityLogService activityLogService)
    {
        _ticketRepo = ticketRepo;
        _sessionRepo = sessionRepo;
        _agentRepo = agentRepo;
        _activityLogService = activityLogService;
    }

    /// <summary>Lista todas as sessões remotas do ticket.</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid ticketId, CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        var sessions = await _sessionRepo.GetByTicketAsync(ticketId, ct);
        return Ok(sessions.Select(s => MapToResponse(s)));
    }

    /// <summary>
    /// Registra o início de uma sessão remota MeshCentral no contexto do ticket.
    /// Retorna a sessão criada com o ID que pode ser usado para encerrar depois.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Start(
        Guid ticketId,
        [FromBody] StartRemoteSessionRequest req,
        CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        string? meshNodeId = req.MeshNodeId;

        // Resolução do meshNodeId a partir do agentId se fornecido
        if (req.AgentId.HasValue && string.IsNullOrWhiteSpace(meshNodeId))
        {
            var agent = await _agentRepo.GetByIdAsync(req.AgentId.Value);
            if (agent is null) return NotFound(new { error = "Agent não encontrado." });
            meshNodeId = agent.MeshCentralNodeId;
        }

        var session = await _sessionRepo.CreateAsync(new TicketRemoteSession
        {
            Id = Guid.NewGuid(),
            TicketId = ticketId,
            AgentId = req.AgentId,
            MeshNodeId = meshNodeId,
            SessionUrl = req.SessionUrl,
            StartedBy = User.Identity?.Name ?? req.StartedBy,
            StartedAt = DateTime.UtcNow,
            Note = req.Note
        }, ct);

        await _activityLogService.LogActivityAsync(
            ticketId, TicketActivityType.RemoteSessionStarted, null, null,
            User.Identity?.Name ?? req.StartedBy,
            $"Sessão remota iniciada" + (string.IsNullOrWhiteSpace(meshNodeId) ? "" : $" no nó {meshNodeId}"));

        return CreatedAtAction(
            nameof(List),
            new { ticketId },
            MapToResponse(session));
    }

    /// <summary>
    /// Encerra uma sessão remota, registrando duração e nota final.
    /// </summary>
    [HttpPatch("{sessionId:guid}/end")]
    public async Task<IActionResult> End(
        Guid ticketId,
        Guid sessionId,
        [FromBody] EndRemoteSessionRequest req,
        CancellationToken ct)
    {
        var session = await _sessionRepo.GetByIdAsync(sessionId, ct);
        if (session is null || session.TicketId != ticketId) return NotFound();

        var endedAt = DateTime.UtcNow;
        session.EndedAt = endedAt;
        session.DurationSeconds = (int)(endedAt - session.StartedAt).TotalSeconds;
        if (!string.IsNullOrWhiteSpace(req.Note)) session.Note = req.Note;

        await _sessionRepo.UpdateAsync(session, ct);

        await _activityLogService.LogActivityAsync(
            ticketId, TicketActivityType.RemoteSessionEnded, null, null,
            User.Identity?.Name,
            $"Sessão remota encerrada (duração: {session.DurationSeconds}s)");

        return Ok(MapToResponse(session));
    }

    private static object MapToResponse(TicketRemoteSession s) => new
    {
        s.Id,
        s.TicketId,
        s.AgentId,
        s.MeshNodeId,
        s.SessionUrl,
        s.StartedBy,
        s.StartedAt,
        s.EndedAt,
        s.DurationSeconds,
        s.Note
    };
}

public record StartRemoteSessionRequest(
    Guid? AgentId,
    string? MeshNodeId,
    string? SessionUrl,
    string? StartedBy,
    string? Note);

public record EndRemoteSessionRequest(string? Note);

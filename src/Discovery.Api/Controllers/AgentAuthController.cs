using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.AiChat;
using Discovery.Core.Cqrs.AgentAuth.Automation;
using Discovery.Core.Cqrs.AgentAuth.Configuration;
using Discovery.Core.Cqrs.AgentAuth.Hardware;
using Discovery.Core.Cqrs.AgentAuth.Knowledge;
using Discovery.Core.Cqrs.AgentAuth.MeshCentral;
using Discovery.Core.Cqrs.AgentAuth.Misc;
using Discovery.Core.Cqrs.AgentAuth.P2P;
using Discovery.Core.Cqrs.AgentAuth.Software;
using Discovery.Core.Cqrs.AgentAuth.Status;
using Discovery.Core.Cqrs.AgentAuth.Tickets;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-auth")]
[AllowAnonymous]
public class AgentAuthController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IAgentRepository _agentRepo;
    private readonly IAiChatService _aiChat;

    public AgentAuthController(IMediator mediator, IAgentRepository agentRepo, IAiChatService aiChat)
    {
        _mediator = mediator;
        _agentRepo = agentRepo;
        _aiChat = aiChat;
    }

    // ── Auth Helpers ──────────────────────────────────────────────────────

    private bool TryGetAgentId(out Guid agentId)
    {
        if (HttpContext.Items["AgentId"] is Guid id) { agentId = id; return true; }
        agentId = Guid.Empty; return false;
    }

    private async Task<(Agent? agent, IActionResult? blocked)> GetAgentOrBlockAsync(Guid agentId, bool allowPending)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return (null, NotFound(new { error = "Agent not found." }));
        if (!allowPending && agent.ZeroTouchPending) return (null, StatusCode(403, new { error = "Agent registration is pending (zero-touch)." }));
        return (agent, null);
    }

    private IActionResult MapResult<T>(Result<T> result, Func<T, IActionResult> onSuccess)
        where T : notnull
        => result.Match(onSuccess, errors => errors[0].Code switch
        {
            "NotFound" => NotFound(new { error = errors[0].Message }),
            "Forbidden" => StatusCode(403, new { error = errors[0].Message }),
            _ => BadRequest(new { error = errors[0].Message })
        });

    // ── Hardware ──────────────────────────────────────────────────────────

    [HttpGet("me/hardware")]
    public async Task<IActionResult> GetHardware()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentHardwareQuery(id)), Ok);
    }

    [HttpPost("me/hardware")]
    public async Task<IActionResult> ReportHardwarePost([FromBody] ReportAgentHardwareCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), _ => Ok());
    }

    [HttpPut("me/hardware")]
    public Task<IActionResult> ReportHardwarePut([FromBody] ReportAgentHardwareCommand cmd) => ReportHardwarePost(cmd);

    // ── Software ──────────────────────────────────────────────────────────

    [HttpGet("me/software")]
    public async Task<IActionResult> GetSoftwareInventory()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentSoftwareQuery(id)), Ok);
    }

    [HttpPost("me/software")]
    public async Task<IActionResult> ReportSoftwarePost([FromBody] ReportAgentSoftwareCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), _ => Ok(new { Message = "Software inventory updated." }));
    }

    [HttpPut("me/software")]
    public Task<IActionResult> ReportSoftwarePut([FromBody] ReportAgentSoftwareCommand cmd) => ReportSoftwarePost(cmd);

    // ── Status ────────────────────────────────────────────────────────────

    [HttpGet("me/realtime/status")]
    public async Task<IActionResult> GetAgentRealtimeStatus()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        return MapResult(await _mediator.Send(new GetAgentRealtimeStatusQuery()), Ok);
    }

    // ── Automation ────────────────────────────────────────────────────────

    [HttpPost("me/automation/policy-sync")]
    public async Task<IActionResult> SyncAutomationPolicy([FromBody] SyncAutomationPolicyCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }, ct), Ok);
    }

    [HttpGet("me/commands")]
    public async Task<IActionResult> GetCommands([FromQuery] int limit = 50)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentCommandsQuery(id, limit)), Ok);
    }

    [HttpPost("me/automation/executions/{commandId:guid}/ack")]
    public async Task<IActionResult> AckAutomationExecution(Guid commandId, [FromBody] AckAutomationExecutionCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, CommandId = commandId }), _ => Ok(new { acknowledged = true, commandId }));
    }

    [HttpPost("me/automation/executions/{commandId:guid}/result")]
    public async Task<IActionResult> CompleteAutomationExecution(Guid commandId, [FromBody] CompleteAutomationExecutionCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, CommandId = commandId }), _ => Ok(new { completed = true, commandId }));
    }

    // ── Configuration ─────────────────────────────────────────────────────

    [HttpGet("me/configuration")]
    public async Task<IActionResult> GetConfiguration()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentConfigurationQuery(id)), Ok);
    }

    [HttpPost("me/tls-mismatch")]
    public async Task<IActionResult> ReportTlsMismatch([FromBody] ReportAgentTlsMismatchCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out _)) return Unauthorized();
        return MapResult(await _mediator.Send(cmd, ct), Ok);
    }

    [HttpGet("me/sync-manifest")]
    public async Task<IActionResult> GetSyncManifest(CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentSyncManifestQuery(id), ct), Ok);
    }

    // ── Tickets ───────────────────────────────────────────────────────────

    [HttpGet("me/tickets")]
    public async Task<IActionResult> GetMyTickets([FromQuery] Guid? workflowStateId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketsQuery(id, workflowStateId)), Ok);
    }

    [HttpGet("me/tickets/{ticketId:guid}")]
    public async Task<IActionResult> GetMyTicket(Guid ticketId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketQuery(id, ticketId)), Ok);
    }

    [HttpPost("me/tickets")]
    public async Task<IActionResult> CreateMyTicket([FromBody] CreateMyTicketCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), dto => CreatedAtAction(nameof(GetMyTicket), new { ticketId = (dto as dynamic)?.Id }, dto));
    }

    [HttpPost("me/tickets/{ticketId:guid}/comments")]
    public async Task<IActionResult> AddMyTicketComment(Guid ticketId, [FromBody] AddMyTicketCommentCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, TicketId = ticketId }), Ok);
    }

    [HttpGet("me/tickets/{ticketId:guid}/comments")]
    public async Task<IActionResult> GetMyTicketComments(Guid ticketId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketCommentsQuery(id, ticketId)), Ok);
    }

    [HttpPatch("me/tickets/{ticketId:guid}/workflow-state")]
    public async Task<IActionResult> UpdateMyTicketWorkflowState(Guid ticketId, [FromBody] UpdateMyTicketWorkflowStateCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, TicketId = ticketId }), Ok);
    }

    [HttpPost("me/tickets/{ticketId:guid}/close")]
    public async Task<IActionResult> CloseAndRateTicket(Guid ticketId, [FromBody] CloseAndRateMyTicketCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, TicketId = ticketId }), Ok);
    }

    // ── MeshCentral ───────────────────────────────────────────────────────

    [HttpPost("me/support/meshcentral/embed-url")]
    public async Task<IActionResult> CreateMeshCentralEmbedUrl([FromBody] CreateMeshCentralEmbedUrlCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), Ok);
    }

    [HttpGet("me/support/meshcentral/install")]
    public async Task<IActionResult> GetMeshCentralInstall()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMeshCentralInstallQuery(id)), Ok);
    }

    // ── P2P ───────────────────────────────────────────────────────────────

    [HttpGet("me/p2p/seed-plan")]
    public async Task<IActionResult> GetP2pSeedPlan(CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentP2pSeedPlanQuery(id), ct), Ok);
    }

    // ── Knowledge ─────────────────────────────────────────────────────────

    [HttpGet("knowledge")]
    public async Task<IActionResult> GetKnowledgeArticles([FromQuery] string? category = null, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetKnowledgeArticlesQuery(id, category), ct), Ok);
    }

    [HttpGet("knowledge/{articleId:guid}")]
    public async Task<IActionResult> GetKnowledgeArticle(Guid articleId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetKnowledgeArticleQuery(id, articleId), ct), Ok);
    }

    // ── Misc ──────────────────────────────────────────────────────────────

    [HttpGet("me")]
    public async Task<IActionResult> GetAgentIdentity()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentIdentityQuery(id)), Ok);
    }

    [HttpGet("me/app-store")]
    public async Task<IActionResult> GetAppStoreEffective([FromQuery] string installationType = "Winget", CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAppStoreEffectiveQuery(id, installationType), ct), Ok);
    }

    [HttpGet("me/custom-fields/runtime")]
    public async Task<IActionResult> GetRuntimeCustomFields([FromQuery] Guid? taskId = null, [FromQuery] Guid? scriptId = null, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetRuntimeCustomFieldsQuery(id, taskId, scriptId), ct), Ok);
    }

    [HttpPost("me/custom-fields/collected")]
    public async Task<IActionResult> UpsertCollectedCustomField([FromBody] UpsertCollectedCustomFieldCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }, ct), Ok);
    }

    [HttpPost("me/zero-touch/deploy-token")]
    public async Task<IActionResult> IssueZeroTouchDeployToken()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new IssueZeroTouchDeployTokenCommand(id)), Ok);
    }

    [HttpGet("me/update/manifest")]
    public async Task<IActionResult> GetAgentUpdateManifest(
        [FromQuery] string? currentVersion = null, [FromQuery] string? platform = null,
        [FromQuery] string? architecture = null, [FromQuery] string? artifactType = null, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentUpdateManifestQuery(id, currentVersion, platform, architecture, artifactType), ct), Ok);
    }

    [HttpGet("me/update/download")]
    public async Task<IActionResult> DownloadAgentUpdate(
        [FromQuery] Guid? releaseId = null, [FromQuery] string? version = null,
        [FromQuery] string? platform = null, [FromQuery] string? architecture = null,
        [FromQuery] string? artifactType = null, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new DownloadAgentUpdateQuery(id, releaseId, version, platform, architecture, artifactType), ct), Ok);
    }

    [HttpPost("me/update/report")]
    public async Task<IActionResult> ReportAgentUpdate([FromBody] AgentUpdateReportRequest report, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new ReportAgentUpdateCommand(id, report), ct), Ok);
    }

    // ── AI Chat ───────────────────────────────────────────────────────────

    [HttpPost("me/ai-chat")]
    public async Task<IActionResult> ChatSync([FromBody] ChatSyncCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }, ct), Ok);
    }

    [HttpPost("me/ai-chat/async")]
    public async Task<IActionResult> ChatAsync([FromBody] ChatAsyncCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }, ct), dto => Accepted(dto));
    }

    [HttpPost("me/ai-chat/stream")]
    public async Task ChatStream([FromBody] ChatAsyncCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var agentId))
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "Agent not authenticated." }, ct);
            return;
        }
        var (_, blocked) = await GetAgentOrBlockAsync(agentId, false);
        if (blocked is not null)
        {
            var obj = (ObjectResult)blocked;
            HttpContext.Response.StatusCode = obj.StatusCode ?? 403;
            await HttpContext.Response.WriteAsJsonAsync(obj.Value ?? new { error = "Agent blocked." }, ct);
            return;
        }

        HttpContext.Response.ContentType = "text/event-stream";
        HttpContext.Response.Headers.Append("Cache-Control", "no-cache");
        HttpContext.Response.Headers.Append("Connection", "keep-alive");
        HttpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            await foreach (var chunk in _aiChat.StreamAsync(agentId, cmd.Message, null, cmd.DepartmentId, ct))
            {
                if (chunk.Type == "error")
                {
                    await HttpContext.Response.WriteAsync($"data: {{\"type\":\"error\",\"error\":\"{EscapeSse(chunk.Error ?? "unknown")}\"}}\n\n", ct);
                    await HttpContext.Response.Body.FlushAsync(ct);
                    return;
                }
                var json = System.Text.Json.JsonSerializer.Serialize(chunk);
                await HttpContext.Response.WriteAsync($"data: {json}\n\n", ct);
                await HttpContext.Response.Body.FlushAsync(ct);
                if (chunk.Type == "done") break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                await HttpContext.Response.WriteAsync($"data: {{\"type\":\"error\",\"error\":\"{EscapeSse(ex.Message)}\"}}\n\n", ct);
        }
    }

    private static string EscapeSse(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    [HttpGet("me/ai-chat/jobs/{jobId}")]
    public async Task<IActionResult> GetAiChatJob(Guid jobId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAiChatJobQuery(id, jobId), ct), Ok);
    }
}
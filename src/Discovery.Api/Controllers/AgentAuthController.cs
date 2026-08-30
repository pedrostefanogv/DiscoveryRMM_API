using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.AiChat;
using Discovery.Core.Cqrs.AgentAuth.Automation;
using Discovery.Core.Cqrs.AgentAuth.Configuration;
using Discovery.Core.Cqrs.AgentAuth.Hardware;
using Discovery.Core.Cqrs.AgentAuth.Knowledge;
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
            "Validation" => BadRequest(new { error = errors[0].Message, field = errors[0].Field }),
            "Internal" => StatusCode(500, new { error = "Erro interno do servidor" }),
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

    // ── P2P ───────────────────────────────────────────────────────────────

    [HttpGet("me/p2p/seed-plan")]
    public async Task<IActionResult> GetP2pSeedPlan(CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentP2pSeedPlanQuery(id), ct), Ok);
    }

    [HttpPost("me/p2p/telemetry")]
    public async Task<IActionResult> IngestP2pTelemetry([FromBody] P2pTelemetryRequest request, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new IngestP2pTelemetryCommand(id, request), ct), Ok);
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

    [HttpGet("knowledge/{articleId:guid}/pages")]
    public async Task<IActionResult> GetKnowledgeArticlePages(Guid articleId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetKnowledgeArticlePagesQuery(id, articleId), ct), Ok);
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
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
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
        return MapResult(await _mediator.Send(cmd with { AgentId = id, ClientIp = ResolveAgentClientIp() }, ct), Ok);
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
    public async Task ChatStream([FromBody] ChatStreamCommand cmd, CancellationToken ct)
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

        HttpContext.Response.ContentType = "text/event-stream; charset=utf-8";
        HttpContext.Response.Headers.Append("Cache-Control", "no-cache");
        HttpContext.Response.Headers.Append("Connection", "keep-alive");
        HttpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        // Preenche o ClientIp do comando (antes era campo morto no contrato).
        cmd = cmd with { ClientIp = ResolveAgentClientIp() };

        try
        {
            // ToolResults != null → round 2+ (agent executou tools)
            // Message != null → round 1 (nova mensagem do usuário)
            var sessionGuid = Guid.TryParse(cmd.SessionId, out var g) ? g : (Guid?)null;
            var toolResults = cmd.ToolResults?.Select(tr => new ToolResultItem(
                tr.CallId,
                tr.Name,
                TruncateToolResult(tr.Name, tr.Result))).ToList();

            // Modo explícito (agentes novos) tem prioridade; fallback para a
            // convenção legada (Message == null → multi-round) em agentes antigos.
            var stream = cmd.Mode switch
            {
                "tool_results" or "a2ui_action" =>
                    _aiChat.StreamMultiRoundAsync(agentId, null, sessionGuid, toolResults, cmd.DepartmentId, cmd.SystemNote, ct),
                "user_message" =>
                    _aiChat.StreamAsync(agentId, cmd.Message ?? string.Empty, sessionGuid, cmd.DepartmentId, cmd.SystemNote, ct),
                _ when cmd.Message != null =>
                    _aiChat.StreamAsync(agentId, cmd.Message, sessionGuid, cmd.DepartmentId, cmd.SystemNote, ct),
                _ =>
                    _aiChat.StreamMultiRoundAsync(agentId, null, sessionGuid, toolResults, cmd.DepartmentId, cmd.SystemNote, ct),
            };

            await foreach (var chunk in stream)
            {
                if (chunk.Type == "error")
                {
                    await WriteSseJsonAsync(HttpContext, new { type = "error", error = chunk.Error ?? "unknown" }, ct);
                    await HttpContext.Response.Body.FlushAsync(ct);
                    return;
                }
                await WriteSseJsonAsync(HttpContext, chunk, ct);
                await HttpContext.Response.Body.FlushAsync(ct);
                if (chunk.Type is "done" or "round_end") break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                await WriteSseJsonAsync(HttpContext, new { type = "error", error = ex.Message }, ct);
        }
    }

    [HttpPost("me/agent-tools/registry")]
    public async Task<IActionResult> RegisterAgentTools([FromBody] RegisterAgentToolsCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var agentId)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(agentId, false);
        if (blocked is not null) return blocked;

        var tools = cmd.Tools.Select(t => new AgentToolRegistration(
            t.Name, t.Description, t.ParametersSchema.GetRawText())).ToList();
        await _aiChat.RegisterAgentToolsAsync(agentId, agent!.SiteId, tools, ct);
        return Ok(new { registered = tools.Count });
    }

    /// <summary>
    /// Ranges IPv4/IPv6 oficiais da Cloudflare (https://www.cloudflare.com/ips/).
    /// Espelha os defaults de ForwardedHeadersOptions em Program.cs — o header
    /// CF-Connecting-IP só é aceito quando a conexão vem de um desses ranges.
    /// </summary>
    private static readonly System.Net.IPNetwork[] CloudflareNetworks =
    [
        new(System.Net.IPAddress.Parse("173.245.48.0"), 20),
        new(System.Net.IPAddress.Parse("103.21.244.0"), 22),
        new(System.Net.IPAddress.Parse("103.22.200.0"), 22),
        new(System.Net.IPAddress.Parse("103.31.4.0"), 22),
        new(System.Net.IPAddress.Parse("141.101.64.0"), 18),
        new(System.Net.IPAddress.Parse("108.162.192.0"), 18),
        new(System.Net.IPAddress.Parse("190.93.240.0"), 20),
        new(System.Net.IPAddress.Parse("188.114.96.0"), 20),
        new(System.Net.IPAddress.Parse("197.234.240.0"), 22),
        new(System.Net.IPAddress.Parse("198.41.128.0"), 17),
        new(System.Net.IPAddress.Parse("162.158.0.0"), 15),
        new(System.Net.IPAddress.Parse("104.16.0.0"), 13),
        new(System.Net.IPAddress.Parse("104.24.0.0"), 14),
        new(System.Net.IPAddress.Parse("172.64.0.0"), 13),
        new(System.Net.IPAddress.Parse("131.0.72.0"), 22),
        new(System.Net.IPAddress.Parse("2400:cb00::"), 32),
        new(System.Net.IPAddress.Parse("2606:4700::"), 32),
        new(System.Net.IPAddress.Parse("2803:f800::"), 32),
        new(System.Net.IPAddress.Parse("2405:b500::"), 32),
        new(System.Net.IPAddress.Parse("2405:8100::"), 32),
        new(System.Net.IPAddress.Parse("2a06:98c0::"), 29),
        new(System.Net.IPAddress.Parse("2c0f:f248::"), 32),
    ];

    /// <summary>
    /// Verifica se o IP remoto pertence a um range da Cloudflare.
    /// </summary>
    private static bool IsCloudflareIp(System.Net.IPAddress? remoteIp)
    {
        if (remoteIp is null) return false;
        foreach (var net in CloudflareNetworks)
        {
            if (net.Contains(remoteIp)) return true;
        }
        return false;
    }

    /// <summary>
    /// Resolve o IP real do agent de forma confiável:
    /// 1. Connection.RemoteIpAddress — já resolvido pelo middleware
    ///    ForwardedHeaders (X-Forwarded-For aceito apenas de proxies confiáveis:
    ///    ranges Cloudflare + localhost, configurados em Program.cs).
    /// 2. CF-Connecting-IP — aceito SOMENTE se a conexão direta veio de um IP
    ///    Cloudflare (impede spoof por clients autenticados).
    /// 3. Fallback: RemoteIpAddress cru.
    /// Nunca lê X-Forwarded-For manualmente (spoofável pelo agent).
    /// </summary>
    private string ResolveAgentClientIp()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;

        // 1. ForwardedHeaders middleware já resolveu o IP real de proxy confiável.
        if (remoteIp is not null && !IsCloudflareIp(remoteIp) && !System.Net.IPAddress.IsLoopback(remoteIp))
        {
            return remoteIp.ToString();
        }

        // 2. Conexão direta da Cloudflare: usa o header oficial CF-Connecting-IP.
        if (IsCloudflareIp(remoteIp))
        {
            var cfIp = HttpContext.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(cfIp) && System.Net.IPAddress.TryParse(cfIp.Trim(), out _))
            {
                return cfIp.Trim();
            }
        }

        // 3. Fallback (desenvolvimento/local sem proxy).
        return remoteIp?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Limite de tamanho por tool result enviado ao LLM (defesa em profundidade:
    /// o agent Go já trunca em 16 KB; aqui garantimos o teto também server-side).
    /// </summary>
    private const int MaxToolResultLength = 32 * 1024;

    /// <summary>
    /// Trunca o resultado de uma tool para MaxToolResultLength. Tenta fechar
    /// estruturas JSON abertas; se o resultado truncado não for JSON válido,
    /// devolve texto cru com marcador (nunca JSON quebrado).
    /// </summary>
    private static string TruncateToolResult(string toolName, string? result)
    {
        var r = result ?? string.Empty;
        if (r.Length <= MaxToolResultLength) return r;

        var cut = r[..MaxToolResultLength];
        var trimmed = cut.TrimEnd(' ', '\t', '\r', '\n', ',');

        // Fecha estruturas JSON abertas (contagem de delimitadores fora de strings).
        var stack = new Stack<char>();
        var inStr = false;
        var esc = false;
        foreach (var c in trimmed)
        {
            if (inStr)
            {
                if (esc) { esc = false; }
                else if (c == '\\') { esc = true; }
                else if (c == '"') { inStr = false; }
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']': if (stack.Count > 0) stack.Pop(); break;
            }
        }

        var closed = trimmed + string.Concat(stack);
        if (inStr) closed += "\"";

        // Só usa a versão fechada se for JSON válido; caso contrário, texto cru.
        if (IsValidJson(closed) && !inStr)
        {
            return closed;
        }
        return cut + $"\n...[truncado pelo servidor; tool={toolName}; total={r.Length} chars]";
    }

    /// <summary>
    /// Validação leve de JSON usando System.Text.Json.
    /// </summary>
    private static bool IsValidJson(string s)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task WriteSseJsonAsync(HttpContext httpContext, object value, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value, SseJsonOptions);
        await httpContext.Response.WriteAsync($"data: {json}\n\n", ct);
    }

    [HttpGet("me/ai-chat/jobs/{jobId}")]
    public async Task<IActionResult> GetAiChatJob(Guid jobId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAiChatJobQuery(id, jobId), ct), Ok);
    }
}
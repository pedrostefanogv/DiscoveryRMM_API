using Discovery.Api.Filters;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.RemoteSessions.Commands;
using Discovery.Core.Cqrs.RemoteSessions.Queries;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Services.Remote.Recording;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints de sessões de acesso remoto nativo (screen, terminal, files, proxy).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/remote-sessions")]
public class RemoteSessionsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITerminalRecordingService _terminalRecordings;
    private readonly IScopeContext _scopeContext;
    private readonly IRemoteSessionManager _sessionManager;
    private readonly ILogger<RemoteSessionsController> _logger;
    private readonly RemoteAccessOptions _options;

    public RemoteSessionsController(
        IMediator mediator,
        ITerminalRecordingService terminalRecordings,
        IScopeContext scopeContext,
        IRemoteSessionManager sessionManager,
        IOptions<RemoteAccessOptions> options,
        ILogger<RemoteSessionsController> logger)
    {
        _mediator = mediator;
        _terminalRecordings = terminalRecordings;
        _scopeContext = scopeContext;
        _sessionManager = sessionManager;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Inicia uma nova sessão de acesso remoto.</summary>
    [HttpPost("{agentId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> StartSession(Guid agentId, [FromBody] StartRemoteSessionCommand cmd, CancellationToken ct = default)
    {
        var rawUserId = HttpContext.Items["UserId"];
        if (rawUserId is not Guid userId || userId == Guid.Empty)
        {
            _logger.LogWarning("[RemoteSession] StartSession: UserId ausente ou vazio no HttpContext. Raw={RawUserId}, IsApiToken={IsApiToken}, Path={Path}",
                rawUserId, HttpContext.Items["IsApiTokenAuth"], HttpContext.Request.Path);
            return Unauthorized(new { error = "User not authenticated." });
        }

        var tenantId = HttpContext.Items["TenantId"] as Guid? ?? Guid.Empty;
        var siteId = HttpContext.Items["SiteId"] as Guid? ?? Guid.Empty;

        var result = await _mediator.Send(cmd with { AgentId = agentId, UserId = userId, TenantId = tenantId, SiteId = siteId }, ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code switch
            {
                "NotFound" => NotFound(new { error = errors[0].Message }),
                "Conflict" => Conflict(new { error = errors[0].Message, code = "agent_update_required" }),
                _ => BadRequest(new { error = errors[0].Message })
            });
    }

    /// <summary>Encerra uma sessão remota ativa.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/stop")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute, AllowPending = true)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> StopSession(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new StopRemoteSessionCommand(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Renova o TTL de uma sessão remota.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/renew")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> RenewSession(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new RenewRemoteSessionCommand(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Lista sessões ativas para um agent.</summary>
    [HttpGet("{agentId:guid}/active")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetActiveSessions(Guid agentId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new GetActiveSessionsQuery(agentId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>
    /// Estado do consentimento (o console aguarda a decisão antes de conectar).
    /// </summary>
    [HttpGet("{agentId:guid}/{sessionId:guid}/consent")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetConsentStatus(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        // Consulta de status aceita QUALQUER estado (inclusive denied/timeout),
        // desde que a sessão pertença ao usuário. O filtro RemoteSessionAuthorize
        // só conhece sessões abertas e esconderia justamente a decisão negativa.
        var session = await _sessionManager.GetRawSessionAsync(sessionId, ct);
        if (session is null || session.AgentId != agentId || session.UserId != userId)
            return NotFound(new { error = "Remote session not found." });

        var status = session.Status == Discovery.Core.Enums.RemoteSessionStatus.PendingConsent
            ? "pending"
            : session.ConsentDecidedAt is null && !session.ConsentRequired
                ? "not_required"
                : session.ConsentOutcome ?? "granted";

        return Ok(new Discovery.Core.Cqrs.RemoteSessions.Commands.RemoteSessionConsentStatusDto(
            session.Id,
            session.Status,
            session.ConsentRequired,
            status,
            session.ConsentDeadlineAt,
            session.ConsentDecidedAt,
            session.ConsentOutcome,
            Reason: null,
            Auto: session.ConsentDecidedAt is not null && session.ConsentOutcome?.StartsWith("timeout", StringComparison.OrdinalIgnoreCase) == true,
            session.ConsentPolicySource,
            PromptBackend: null,
            DecisionGraceSeconds: Math.Max(0, _options.Consent.LateDecisionGraceSeconds)));
    }

    /// <summary>Obtém credenciais NATS (JWT + NKey) para o viewer se conectar ao stream.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/nats-credentials")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> GetSessionCredentials(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var raw = await _sessionManager.GetRawSessionAsync(sessionId, ct);
        if (raw is not null && raw.Status == Discovery.Core.Enums.RemoteSessionStatus.PendingConsent)
            return Conflict(new { error = "Session is waiting for user consent.", reason = "consent_pending" });
        var result = await _mediator.Send(new GetSessionCredentialsQuery(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Inicia a gravação de uma sessão remota.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/recording/start")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> StartRecording(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new StartRecordingCommand(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Para a gravação de uma sessão remota.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/recording/stop")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> StopRecording(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new StopRecordingCommand(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Obtém URL de download da gravação de uma sessão.</summary>
    [HttpGet("{agentId:guid}/{sessionId:guid}/recording/download")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.View)]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetRecordingDownload(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new GetRecordingDownloadQuery(agentId, sessionId, userId), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    // ── Gravação de terminal (NDJSON) ──

    /// <summary>Obtém os metadados da gravação de terminal de uma sessão.</summary>
    [HttpGet("{agentId:guid}/recording/terminal")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetTerminalRecording(
        Guid agentId,
        [FromQuery] Guid sessionId,
        CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
            return BadRequest(new { error = "sessionId é obrigatório." });

        if (await AuthorizeTerminalRecordingAsync(agentId, sessionId, ActionType.View, ct) is { } denied)
            return denied;

        var recording = await _terminalRecordings.GetAsync(agentId, sessionId, ct);
        return recording is null
            ? NotFound(new { error = "Terminal recording not found." })
            : Ok(recording);
    }

    /// <summary>Baixa o NDJSON da gravação de terminal (um frame JSON por linha).</summary>
    [HttpGet("{agentId:guid}/recording/terminal/download")]
    [RequirePermission(ResourceType.Agents, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> DownloadTerminalRecording(
        Guid agentId,
        [FromQuery] Guid sessionId,
        CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
            return BadRequest(new { error = "sessionId é obrigatório." });

        if (await AuthorizeTerminalRecordingAsync(agentId, sessionId, ActionType.View, ct) is { } denied)
            return denied;

        var download = await _terminalRecordings.OpenDownloadAsync(agentId, sessionId, ct);
        if (download is null)
            return NotFound(new { error = "Terminal recording not found." });

        // Gravação enviada ao S3: 302 para a URL pré-assinada (TTL curto), sem
        // transmitir o NDJSON pela API. Modo Local continua como stream.
        if (!string.IsNullOrWhiteSpace(download.S3Url))
            return Redirect(download.S3Url);

        return File(download.Content!, "application/x-ndjson", download.FileName);
    }

    /// <summary>Exclui a gravação de terminal (arquivo + metadados).</summary>
    [HttpDelete("{agentId:guid}/recording/terminal")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> DeleteTerminalRecording(
        Guid agentId,
        [FromQuery] Guid sessionId,
        CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
            return BadRequest(new { error = "sessionId é obrigatório." });

        if (await AuthorizeTerminalRecordingAsync(agentId, sessionId, ActionType.Execute, ct) is { } denied)
            return denied;

        var deleted = await _terminalRecordings.DeleteAsync(agentId, sessionId, ct);
        return deleted ? NoContent() : NotFound(new { error = "Terminal recording not found." });
    }

    /// <summary>
    /// Autoriza o acesso à gravação de terminal de uma sessão.
    ///
    /// Ordem importa: primeiro confirma que a gravação existe (404 quando não existe,
    /// sem revelar sessões de outros tenants) e só então recorta o tenant — acesso
    /// global OU permissão no cliente/site da sessão, senão 403. A decisão em si fica
    /// em <see cref="TerminalRecordingAccessPolicy"/> para ser testável com fakes.
    /// </summary>
    private async Task<IActionResult?> AuthorizeTerminalRecordingAsync(
        Guid agentId,
        Guid sessionId,
        ActionType action,
        CancellationToken ct)
    {
        var tenant = await _terminalRecordings.GetTenantAsync(agentId, sessionId, ct);
        if (tenant is null)
            return NotFound(new { error = "Terminal recording not found." });

        var access = await _scopeContext.GetAccessAsync(ResourceType.Agents, action);
        if (!TerminalRecordingAccessPolicy.Allows(access, tenant))
        {
            _logger.LogWarning(
                "[RemoteSession] Acesso negado à gravação de terminal {SessionId} (agent {AgentId}, tenant {ClientId}, site {SiteId})",
                sessionId, agentId, tenant.ClientId, tenant.SiteId);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Permissão insuficiente para esta sessão." });
        }

        return null;
    }

    /// <summary>Altera qualidade/codec/FPS de uma sessão ativa. Suporta modo Auto para adaptação dinâmica.</summary>
    [HttpPut("{agentId:guid}/{sessionId:guid}/quality")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> ChangeQuality(Guid agentId, Guid sessionId, [FromBody] ChangeRemoteSessionQualityCommand cmd, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(cmd with { AgentId = agentId, SessionId = sessionId, UserId = userId }, ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Exclui a gravação de uma sessão (LGPD Art. 18).</summary>
    [HttpDelete("{agentId:guid}/{sessionId:guid}/recording")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> DeleteRecording(Guid agentId, Guid sessionId, CancellationToken ct = default)
    {
        // Exclusão assíncrona via recording service
        // O handler de delete será implementado junto com o storage completo (Fase 8)
        return NoContent();
    }

    private Guid GetUserId()
    {
        if (HttpContext.Items["UserId"] is Guid uid) return uid;
        if (HttpContext.Items["UserId"] is string uidStr && Guid.TryParse(uidStr, out var parsed)) return parsed;
        return Guid.Empty;
    }

    // ── Terminal Multi-Tab ──

    /// <summary>Cria uma nova aba de terminal na sessão.</summary>
    [HttpPost("{agentId:guid}/{sessionId:guid}/terminal/tabs")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> CreateTerminalTab(Guid agentId, Guid sessionId, [FromBody] CreateTerminalTabRequest req, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new CreateTerminalTabCommand(agentId, sessionId, userId, req.Shell, req.Cols, req.Rows), ct);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Fecha uma aba de terminal.</summary>
    [HttpDelete("{agentId:guid}/{sessionId:guid}/terminal/tabs/{tabId:guid}")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> CloseTerminalTab(Guid agentId, Guid sessionId, Guid tabId, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var result = await _mediator.Send(new CloseTerminalTabCommand(agentId, sessionId, userId, tabId), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound" ? NotFound(new { error = errors[0].Message }) : BadRequest(new { error = errors[0].Message }));
    }

    /// <summary>Lista shells disponíveis no agent (inclui WSL se instalado).</summary>
    [HttpGet("{agentId:guid}/{sessionId:guid}/terminal/shells")]
    [RemoteSessionAuthorize(RequiredAction = ActionType.View)]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public IActionResult GetAvailableShells(Guid agentId, Guid sessionId)
    {
        // Retorna shells padrão; WSL availability vem do agent via NATS term.ready
        return Ok(new
        {
            shells = new[] { "powershell", "cmd" },
            note = "WSL availability is reported by the agent via NATS term.ready event"
        });
    }
}

/// <summary>Request para criar uma nova aba de terminal.</summary>
public record CreateTerminalTabRequest(string Shell = "powershell", int Cols = 120, int Rows = 40);

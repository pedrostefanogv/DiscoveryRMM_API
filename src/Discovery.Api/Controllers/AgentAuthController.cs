using Discovery.Core.Cqrs.AgentAuth.Configuration;
using Discovery.Core.Cqrs.AgentAuth.Misc;
using Discovery.Core.Cqrs.AgentAuth.P2P;
using Discovery.Core.Cqrs.AgentAuth.Status;
using Discovery.Core.Cqrs.Notes.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

public class AgentAuthController : AgentAuthControllerBase
{
    private readonly IP2pService _p2pService;
    private readonly IAgentCaService _agentCaService;
    private readonly ISiteRepository _siteRepo;

    public AgentAuthController(
        IMediator mediator,
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IP2pService p2pService,
        IAgentCaService agentCaService)
        : base(mediator, agentRepo)
    {
        _siteRepo = siteRepo;
        _p2pService = p2pService;
        _agentCaService = agentCaService;
    }

    // ── Identity & Status ─────────────────────────────────────────────────

    [HttpGet("me/mtls/ca")]
    public IActionResult GetMtlsCaCertificate()
    {
        if (!TryGetAgentId(out _)) return Unauthorized();
        return Ok(new { caCertificatePem = _agentCaService.GetCaCertificatePem() });
    }

    public sealed record AgentMtlsEnrollRequest(string CsrPem);

    [HttpPost("me/mtls/enroll")]
    public async Task<IActionResult> EnrollMtls([FromBody] AgentMtlsEnrollRequest request, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        if (request is null || string.IsNullOrWhiteSpace(request.CsrPem))
            return BadRequest(new { error = "CsrPem é obrigatório." });

        var site = await _siteRepo.GetByIdAsync(agent!.SiteId);
        var clientId = site?.ClientId ?? Guid.Empty;
        try
        {
            var result = _agentCaService.SignAgentCsr(agent.Id, clientId, request.CsrPem);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetAgentIdentity()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentIdentityQuery(id)), Ok);
    }

    [HttpGet("me/realtime/status")]
    public async Task<IActionResult> GetAgentRealtimeStatus()
    {
        if (!TryGetAgentId(out _)) return Unauthorized();
        return MapResult(await _mediator.Send(new GetAgentRealtimeStatusQuery()), Ok);
    }

    // ── Configuration & Sync ──────────────────────────────────────────────

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

    // ── P2P ───────────────────────────────────────────────────────────────

    [HttpGet("me/p2p/seed-plan")]
    public async Task<IActionResult> GetP2pSeedPlan(CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentP2pSeedPlanQuery(id), ct), Ok);
    }

    /// <summary>
    /// Bootstrap P2P via nuvem: o agent PUXA a lista de peers online do seu
    /// cliente (HeartbeatCache/Redis) com endereçamento libp2p. Gateado pelo
    /// toggle CloudBootstrapEnabled (disabled=true = sucesso com zero peers).
    /// </summary>
    [HttpPost("me/p2p/bootstrap")]
    public async Task<IActionResult> BootstrapP2p([FromBody] P2pCloudBootstrapRequest? request, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentP2pCloudBootstrapQuery(id, request), ct), Ok);
    }

    [HttpPost("me/p2p/telemetry")]
    public async Task<IActionResult> IngestP2pTelemetry([FromBody] P2pTelemetryRequest request, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        // Rate limit server-side (5 req/10 min por agente). Estava implementado em
        // P2pService.CheckTelemetryRateLimitAsync mas nunca era chamado — o agente
        // tinha toda a lógica de retry para 429 que nunca acontecia.
        var retryAfterSeconds = await _p2pService.CheckTelemetryRateLimitAsync(id, ct);
        if (retryAfterSeconds > 0)
        {
            Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "rate limit de telemetria excedido",
                code = "RATE_LIMIT_EXCEEDED"
            });
        }

        return MapResult(await _mediator.Send(new IngestP2pTelemetryCommand(id, request), ct), Ok);
    }

    // ── Custom Fields, Notes, Zero-Touch & Self-Update ────────────────────

    [HttpGet("me/custom-fields/runtime")]
    public async Task<IActionResult> GetRuntimeCustomFields([FromQuery] Guid? taskId = null, [FromQuery] Guid? scriptId = null, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetRuntimeCustomFieldsQuery(id, taskId, scriptId), ct), Ok);
    }

    [HttpPost("me/custom-fields/collected")]
    public async Task<IActionResult> UpsertCollectedCustomField([FromBody] AgentCollectedCustomFieldValueRequest request, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new UpsertCollectedCustomFieldCommand(id, request), ct), Ok);
    }

    /// <summary>
    /// Cria uma nota no próprio agent. Usado por tarefas com "gravar saída como
    /// nota" para guardar o resultado/coleta no histórico do agente.
    /// </summary>
    [HttpPost("me/notes")]
    public async Task<IActionResult> CreateAgentNote([FromBody] CreateNoteRequest request, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var cmd = new CreateNoteCommand(null, null, id, request.Content, "agent", request.IsPinned);
        return MapResult(await _mediator.Send(cmd, ct), Ok);
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

    // ── Compatibility Forwarding Helpers ──────────────────────────────────

    internal static string ResolveCustomInstallerFileName(string? fileName, string? fileObjectKey, string packageId)
        => AgentAuthKnowledgeAndStoreController.ResolveCustomInstallerFileName(fileName, fileObjectKey, packageId);

    internal static (string? Error, int StatusCode) ValidateAiChatImages(
        IReadOnlyList<string> images, TicketAttachmentSettings settings)
        => AgentAuthAiChatController.ValidateAiChatImages(images, settings);

    internal static long EstimateBase64DecodedBytes(string base64)
        => AgentAuthAiChatController.EstimateBase64DecodedBytes(base64);

    internal static string TruncateToolResult(string toolName, string? result)
        => AgentAuthAiChatController.TruncateToolResult(toolName, result);

    internal static (int Status, string? Detail) DescribeChatStreamFailure(Exception ex)
        => AgentAuthAiChatController.DescribeChatStreamFailure(ex);
}

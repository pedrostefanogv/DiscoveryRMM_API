using System.Net;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs.AgentUpdates.Commands;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-updates")]
public class AgentUpdatesController(IMediator mediator, AgentRebuildCoordinator rebuildCoordinator) : ControllerBase
{
    private static async Task<IActionResult?> EnsureAgentAccessAsync(
        Guid agentId,
        ActionType action,
        IScopeContext scopeContext,
        IAgentRepository agentRepo,
        ISiteRepository siteRepo)
    {
        var agent = await agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return new NotFoundObjectResult(new { error = "Agent not found." });

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Deployment, action);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return new ObjectResult(new { error = "Você não tem acesso a este agente." }) { StatusCode = StatusCodes.Status403Forbidden };

        return null;
    }

    [HttpGet("build/current")]
    [RequirePermission(ResourceType.Deployment, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> GetCurrentBuild([FromQuery] string? platform, [FromQuery] string? architecture, [FromQuery] string? artifactType, CancellationToken ct)
    {
        var r = await mediator.Send(new GetCurrentAgentBuildQuery(platform, architecture, artifactType), ct);
        return r.Match<IActionResult>(Ok, e => NotFound(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    /// <summary>
    /// Upload a new agent build artifact and publish it as the current active build.
    /// Accepts multipart/form-data with metadata fields and the binary file.
    /// </summary>
    [HttpPost("build/refresh")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit, ScopeSource.Global)]
    [RequestSizeLimit(500_000_000)]
    public async Task<IActionResult> RefreshBuild(
        [FromForm] RefreshAgentBuildFormRequest request,
        IFormFile file,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { errors = new[] { new { Code = "FILE_REQUIRED", Message = "Build binary file is required." } } });

        await using var stream = file.OpenReadStream();
        var cmd = new RefreshAgentBuildCommand(
            request.Version, request.Platform, request.Architecture,
            request.ArtifactType, file.FileName, file.ContentType,
            stream, request.SignatureThumbprint, request.CommitHash, null);

        var r = await mediator.Send(cmd, ct);
        return r.Match<IActionResult>(Ok, e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    /// <summary>
    /// Triggers a full agent rebuild from source (sync + build binary + publish stage2 installer).
    /// Does not require file upload — the server rebuilds from the configured agent repository.
    ///
    /// Assíncrono por padrão: responde 202 imediatamente e o build roda em background
    /// (acompanhe em GET build/rebuild/status). Disparos concorrentes NÃO reiniciam o
    /// build: quem chega durante um rebuild em andamento apenas adere a ele
    /// (alreadyRunning=true) — a retentativa do script de update não recomeça o build.
    ///
    /// ?wait=true preserva o contrato síncrono antigo (usado pela UI): espera a
    /// conclusão e devolve o build publicado. Mesmo nesse modo o build NÃO é
    /// cancelado se o cliente HTTP desconectar.
    ///
    /// Access control:
    /// - Direct loopback connections (scripts running on the server) are allowed without authentication.
    /// - Authenticated admin users calling through the frontend/proxy are also allowed.
    /// - Unauthenticated requests from non-loopback IPs are rejected.
    ///
    /// Não usa [RequirePermission] de propósito: o fluxo de script em localhost é anônimo.
    /// </summary>
    [HttpPost("build/rebuild")]
    [AllowAnonymous]
    public async Task<IActionResult> Rebuild([FromQuery] bool wait = false, CancellationToken ct = default)
    {
        if (EnsureRebuildAccess() is { } denied)
            return denied;

        var actor = IsLoopbackWithoutProxyHeaders()
            ? "localhost-script"
            : $"user:{HttpContext.Items["UserId"]}";

        var (started, status) = rebuildCoordinator.TryStart(actor);
        var runId = status.RunId;

        if (!wait)
        {
            return Accepted(new
            {
                message = started
                    ? "Agent rebuild started in the background."
                    : "Agent rebuild already in progress; joined the running build.",
                runId,
                state = status.State.ToString(),
                actor = status.Actor,
                startedAt = status.StartedAt,
                alreadyRunning = !started,
                statusUrl = RebuildStatusUrl
            });
        }

        if (runId is null)
        {
            return StatusCode(500, new
            {
                errors = new[] { new { Code = "REBUILD_NOT_STARTED", Message = "Could not start the agent rebuild." } }
            });
        }

        AgentRebuildSnapshot final;
        try
        {
            final = await rebuildCoordinator.WaitForCompletionAsync(runId.Value, ct);
        }
        catch (OperationCanceledException)
        {
            // Cliente desconectou: o build continua em background de propósito.
            return Accepted(new
            {
                message = "Client disconnected; the rebuild continues in the background.",
                runId,
                state = rebuildCoordinator.GetStatus().State.ToString(),
                statusUrl = RebuildStatusUrl
            });
        }

        if (final.State == AgentRebuildState.Succeeded && final.Build is { } build)
        {
            return Ok(new
            {
                message = "Agent rebuild completed successfully.",
                buildId = build.Id,
                version = build.Version,
                platform = build.Platform,
                architecture = build.Architecture,
                fileName = build.FileName,
                sha256 = build.Sha256,
                publishedAt = build.PublishedAt,
                runId
            });
        }

        return StatusCode(500, new
        {
            errors = new[] { new { Code = "REBUILD_FAILED", Message = final.Error ?? "Agent rebuild failed." } }
        });
    }

    /// <summary>
    /// Status do rebuild do agente: se há build em andamento e o resultado do último run.
    /// Mesma regra de acesso do POST (loopback direto ou admin autenticado).
    /// </summary>
    [HttpGet("build/rebuild/status")]
    [AllowAnonymous]
    public IActionResult GetRebuildStatus()
    {
        if (EnsureRebuildAccess() is { } denied)
            return denied;

        var status = rebuildCoordinator.GetStatus();
        return Ok(new
        {
            runId = status.RunId,
            state = status.State.ToString(),
            running = status.IsRunning,
            actor = status.Actor,
            startedAt = status.StartedAt,
            finishedAt = status.FinishedAt,
            elapsedSeconds = status.ElapsedSeconds,
            error = status.Error,
            build = status.Build is null ? null : new
            {
                buildId = status.Build.Id,
                version = status.Build.Version,
                platform = status.Build.Platform,
                architecture = status.Build.Architecture,
                fileName = status.Build.FileName,
                sha256 = status.Build.Sha256,
                publishedAt = status.Build.PublishedAt
            }
        });
    }

    private const string RebuildStatusUrl = "/api/v1/agent-updates/build/rebuild/status";

    /// <summary>
    /// Conexão direta de loopback (scripts na própria máquina). Headers de proxy
    /// desqualificam: request vindo pelo Nginx não é confiável como local.
    /// </summary>
    private bool IsLoopbackWithoutProxyHeaders()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        var hasProxyHeaders = HttpContext.Request.Headers.ContainsKey("X-Forwarded-For")
                           || HttpContext.Request.Headers.ContainsKey("X-Forwarded-Host")
                           || HttpContext.Request.Headers.ContainsKey("X-Real-IP");
        return remoteIp is not null && IPAddress.IsLoopback(remoteIp) && !hasProxyHeaders;
    }

    private IActionResult? EnsureRebuildAccess()
    {
        // Loopback direto (script) OU usuário autenticado (frontend via proxy).
        var authorized = IsLoopbackWithoutProxyHeaders() || HttpContext.Items["UserId"] is Guid;
        if (authorized)
            return null;

        return StatusCode(403, new
        {
            message = "Acesso negado. Este endpoint requer conexão direta de localhost ou autenticação de administrador."
        });
    }

    [HttpGet("agents/{agentId:guid}/events")]
    [RequirePermission(ResourceType.Deployment, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetAgentEvents(
        Guid agentId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.View, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var r = await mediator.Send(new ListAgentUpdateEventsQuery(agentId, limit), ct);
        return r.Match<IActionResult>(Ok, e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost("agents/{agentId:guid}/force-update")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> ForceUpdate(
        Guid agentId,
        [FromServices] IScopeContext scopeContext,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] ISiteRepository siteRepo,
        [FromBody] ForceAgentUpdateCommand? cmd = null,
        CancellationToken ct = default)
    {
        if (await EnsureAgentAccessAsync(agentId, ActionType.Edit, scopeContext, agentRepo, siteRepo) is { } denied)
            return denied;

        var command = (cmd ?? new ForceAgentUpdateCommand(agentId, null, null)) with { AgentId = agentId };
        var r = await mediator.Send(command, ct);
        return r.Match<IActionResult>(_ => Ok(new { triggered = true }), e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }
}

/// <summary>
/// Form request model for POST /agent-updates/build/refresh (multipart/form-data).
/// The binary file is sent as a separate IFormFile field named "file".
/// </summary>
public sealed record RefreshAgentBuildFormRequest
{
    public string Version { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string ArtifactType { get; init; } = "Installer";
    public string? SignatureThumbprint { get; init; }
    public string? CommitHash { get; init; }
}

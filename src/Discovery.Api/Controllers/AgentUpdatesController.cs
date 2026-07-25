using System.Net;
using Discovery.Core.Cqrs.AgentUpdates.Commands;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-updates")]
public class AgentUpdatesController(IMediator mediator) : ControllerBase
{
    [HttpGet("build/current")]
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
    /// Access control:
    /// - Direct loopback connections (scripts running on the server) are allowed without authentication.
    /// - Authenticated admin users calling through the frontend/proxy are also allowed.
    /// - Unauthenticated requests from non-loopback IPs are rejected.
    /// </summary>
    [HttpPost("build/rebuild")]
    [AllowAnonymous]
    public async Task<IActionResult> Rebuild(CancellationToken ct)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;

        // Allow direct loopback connections (scripts running on the server).
        var hasProxyHeaders = HttpContext.Request.Headers.ContainsKey("X-Forwarded-For")
                           || HttpContext.Request.Headers.ContainsKey("X-Forwarded-Host")
                           || HttpContext.Request.Headers.ContainsKey("X-Real-IP");
        var isLocalhost = remoteIp is not null && IPAddress.IsLoopback(remoteIp) && !hasProxyHeaders;

        // Allow authenticated users (frontend via proxy with valid JWT or API key).
        var isAuthenticated = HttpContext.Items["UserId"] is Guid;

        if (!isLocalhost && !isAuthenticated)
        {
            return StatusCode(403, new
            {
                message = "Acesso negado. Este endpoint requer conexão direta de localhost ou autenticação de administrador."
            });
        }

        var actor = isLocalhost ? "localhost-script" : $"user:{HttpContext.Items["UserId"]}";
        var r = await mediator.Send(new RebuildAgentCommand(actor), ct);
        return r.Match<IActionResult>(
            build => Ok(new
            {
                message = "Agent rebuild completed successfully.",
                buildId = build.Id,
                version = build.Version,
                platform = build.Platform,
                architecture = build.Architecture,
                fileName = build.FileName,
                sha256 = build.Sha256,
                publishedAt = build.PublishedAt
            }),
            e => StatusCode(500, new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpGet("agents/{agentId:guid}/events")]
    public async Task<IActionResult> GetAgentEvents(Guid agentId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var r = await mediator.Send(new ListAgentUpdateEventsQuery(agentId, limit), ct);
        return r.Match<IActionResult>(Ok, e => BadRequest(new { errors = e.Select(x => new { x.Code, x.Message }) }));
    }

    [HttpPost("agents/{agentId:guid}/force-update")]
    public async Task<IActionResult> ForceUpdate(
        Guid agentId,
        [FromBody] ForceAgentUpdateCommand? cmd = null,
        CancellationToken ct = default)
    {
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

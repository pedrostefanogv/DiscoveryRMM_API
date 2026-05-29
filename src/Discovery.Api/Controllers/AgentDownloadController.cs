using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Public download endpoint for the latest agent stage2 installer (windows/amd64).
/// Used by the bootstrap installer (minimal installer) and future self-update flow.
/// Serves the most recent active build — no token or fileId needed.
/// The binary itself contains no embedded secrets; security is enforced by the deploy token
/// in the bootstrap wrapper and by the agent's own registration flow.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/download")]
[AllowAnonymous]
public class AgentDownloadController : ControllerBase
{
    private readonly IAgentUpdateService _agentUpdateService;
    private readonly IObjectStorageProviderFactory _storageProviderFactory;
    private readonly ILoggingService _loggingService;
    private readonly ILogger<AgentDownloadController> _logger;

    public AgentDownloadController(
        IAgentUpdateService agentUpdateService,
        IObjectStorageProviderFactory storageProviderFactory,
        ILoggingService loggingService,
        ILogger<AgentDownloadController> logger)
    {
        _agentUpdateService = agentUpdateService;
        _storageProviderFactory = storageProviderFactory;
        _loggingService = loggingService;
        _logger = logger;
    }

    /// <summary>
    /// Downloads the latest agent stage2 installer for windows/amd64.
    /// Returns 404 if no build has been published yet (run refresh-build first).
    /// Returns 503 if the object storage is temporarily unreachable.
    /// </summary>
    [HttpGet("agent")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DownloadLatestAgent(CancellationToken cancellationToken)
    {
        var build = await _agentUpdateService.GetCurrentBuildAsync(
            platform: "windows",
            architecture: "amd64",
            artifactType: AgentReleaseArtifactType.Installer,
            cancellationToken: cancellationToken);

        if (build is null)
        {
            return NotFound(new { error = "No agent build is currently available. Trigger refresh-build to publish." });
        }

        try
        {
            var storage = await _storageProviderFactory.CreateObjectStorageServiceAsync(cancellationToken);
            var stream = await storage.DownloadAsync(build.StorageObjectKey, cancellationToken);

            var clientIp = ResolveClientIp();
            var userAgent = HttpContext.Request.Headers["User-Agent"].FirstOrDefault() ?? "unknown";

            _logger.LogInformation(
                "Agent download served: version={Version} sha256={Sha256} size={Size} ip={ClientIp} ua={UserAgent}",
                build.Version, build.Sha256, build.SizeBytes, clientIp, userAgent);

            // Audit log — fire-and-forget to avoid blocking the download response
            _ = _loggingService.LogInfoAsync(
                LogType.System,
                LogSource.Api,
                "Agent stage2 installer download",
                new
                {
                    build.Version,
                    build.Platform,
                    build.Architecture,
                    build.FileName,
                    build.Sha256,
                    build.SizeBytes,
                    ClientIp = clientIp,
                    UserAgent = userAgent,
                    TraceId = HttpContext.TraceIdentifier
                },
                cancellationToken: CancellationToken.None);

            var fileName = build.FileName ?? "discovery-agent-install.exe";
            var contentType = build.ContentType;
            if (string.IsNullOrWhiteSpace(contentType))
                contentType = "application/x-msdownload";

            return File(stream, contentType, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to serve agent download: version={Version} objectKey={ObjectKey}",
                build.Version, build.StorageObjectKey);

            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Agent binary is temporarily unavailable. Please try again later." });
        }
    }

    private string ResolveClientIp()
    {
        var cfConnectingIp = HttpContext.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cfConnectingIp))
            return cfConnectingIp;

        var xForwardedFor = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xForwardedFor))
        {
            var firstIp = xForwardedFor.Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(firstIp))
                return firstIp;
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

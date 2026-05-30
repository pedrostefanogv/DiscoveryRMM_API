using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

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
    private readonly IAgentPackageService _agentPackageService;
    private readonly IConfiguration _configuration;
    private readonly ILoggingService _loggingService;
    private readonly ILogger<AgentDownloadController> _logger;

    public AgentDownloadController(
        IAgentUpdateService agentUpdateService,
        IAgentPackageService agentPackageService,
        IConfiguration configuration,
        ILoggingService loggingService,
        ILogger<AgentDownloadController> logger)
    {
        _agentUpdateService = agentUpdateService;
        _agentPackageService = agentPackageService;
        _configuration = configuration;
        _loggingService = loggingService;
        _logger = logger;
    }

    /// <summary>
    /// Downloads the latest agent stage2 installer for windows/amd64.
    /// Returns 404 if no build has been published yet (run refresh-build first).
    /// Returns 503 if the local stage2 artifact is unavailable.
    /// </summary>
    [HttpHead("agent")]
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
            if (string.IsNullOrWhiteSpace(build.StorageObjectKey))
                throw new InvalidOperationException("Current build does not include a local storage key.");

            var localArtifactPath = ResolveStage2ArtifactPath(build.StorageObjectKey);
            if (!System.IO.File.Exists(localArtifactPath))
            {
                _logger.LogWarning(
                    "Current build artifact is missing locally: version={Version} objectKey={ObjectKey}",
                    build.Version,
                    build.StorageObjectKey);

                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { error = "Agent binary is not available on this node. Trigger refresh-build or restart the API." });
            }

            var stream = new FileStream(localArtifactPath, FileMode.Open, FileAccess.Read, FileShare.Read);

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
                "Failed to serve local agent download: version={Version} objectKey={ObjectKey}",
                build.Version, build.StorageObjectKey);

            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Agent binary is temporarily unavailable. Please try again later." });
        }
    }

    /// <summary>
    /// Downloads the generic (zero-touch) agent installer for windows/amd64.
    /// No URL or deploy token is embedded — the agent uses P2P auto-provisioning
    /// to discover the server and self-register after installation.
    /// Result is cached in memory (default 30 min). Pass ?forceRebuild=true to bypass cache.
    /// </summary>
    [HttpGet("agent/generic")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DownloadGenericAgent(
        [FromQuery] bool forceRebuild = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (content, fileName) = await _agentPackageService.BuildGenericInstallerAsync(forceRebuild);

            var clientIp = ResolveClientIp();
            var userAgent = HttpContext.Request.Headers["User-Agent"].FirstOrDefault() ?? "unknown";

            _logger.LogInformation(
                "Generic (zero-touch) agent download served: fileName={FileName} size={Size} ip={ClientIp} ua={UserAgent}",
                fileName, content.Length, clientIp, userAgent);

            // Audit log — fire-and-forget
            _ = _loggingService.LogInfoAsync(
                LogType.System,
                LogSource.Api,
                "Generic (zero-touch) agent installer download",
                new
                {
                    FileName = fileName,
                    SizeBytes = content.Length,
                    ClientIp = clientIp,
                    UserAgent = userAgent,
                    TraceId = HttpContext.TraceIdentifier
                },
                cancellationToken: CancellationToken.None);

            return File(content, "application/x-msdownload", fileName);
        }
        catch (FileNotFoundException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Installer resources are not available on this server.", detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Installer build failed.", detail = ex.Message });
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

    private string ResolveStage2ArtifactPath(string objectKey)
    {
        var rootPath = ResolveStage2ArtifactsRootPath();
        var normalizedObjectKey = objectKey
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, normalizedObjectKey));
        var rootPrefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(rootPrefix, pathComparison))
            throw new InvalidOperationException("Current build points to an invalid local path.");

        return fullPath;
    }

    private string ResolveStage2ArtifactsRootPath()
    {
        var configuredPath = _configuration["AgentPackage:Stage2ArtifactsPath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath.Trim());

        var discoveryBase = Environment.GetEnvironmentVariable("DISCOVERY_API_BASE");
        if (!string.IsNullOrWhiteSpace(discoveryBase))
            return Path.GetFullPath(Path.Combine(discoveryBase.Trim(), "shared", "agent-update-builds"));

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "app_data", "agent-update-builds"));
    }
}

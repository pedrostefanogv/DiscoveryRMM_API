using Asp.Versioning;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Public download endpoints for agent installers.
/// Rate-limited via the /api/v1/download partition configured in RateLimitingServiceCollectionExtensions.
/// No authentication required — these are called by bootstrap installers and agents during self-update.
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/download/agent")]
public class DownloadController : ControllerBase
{
    private readonly IAgentUpdateService _agentUpdateService;
    private readonly IAgentPackageService _agentPackageService;
    private readonly ILoggingService _loggingService;
    private readonly ILogger<DownloadController> _logger;

    public DownloadController(
        IAgentUpdateService agentUpdateService,
        IAgentPackageService agentPackageService,
        ILoggingService loggingService,
        ILogger<DownloadController> logger)
    {
        _agentUpdateService = agentUpdateService;
        _agentPackageService = agentPackageService;
        _loggingService = loggingService;
        _logger = logger;
    }

    /// <summary>
    /// Serves the stage2 (full) NSIS installer for the current active build.
    /// Called by: bootstrap installers, agent self-update flow.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetStage2(
        [FromQuery] string? platform = null,
        [FromQuery] string? architecture = null,
        CancellationToken ct = default)
    {
        var localPath = await _agentUpdateService.GetCurrentBuildLocalPathAsync(platform, architecture, artifactType: null, ct);

        if (localPath is null || !System.IO.File.Exists(localPath))
        {
            _logger.LogWarning("Stage2 download requested but no current build found (platform={Platform}, arch={Architecture})", platform, architecture);
            return NotFound(new { message = "No active agent build available." });
        }

        var fileName = Path.GetFileName(localPath);
        var contentType = GetContentType(fileName);
        var fileSize = new FileInfo(localPath).Length;

        _logger.LogInformation("Serving stage2 installer: {FileName} ({Size} bytes)", fileName, fileSize);

        await _loggingService.LogInfoAsync(
            LogType.Agent,
            LogSource.Api,
            "deploy.installer.stage2_download",
            new { fileName, fileSize, platform, architecture },
            cancellationToken: ct);

        return PhysicalFile(localPath, contentType, fileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// Serves the generic (zero-touch) NSIS installer.
    /// No deploy token or server URL embedded — the agent discovers the server via peer discovery and self-registers.
    /// Called by: frontend deploy page (zero-touch install button).
    /// </summary>
    [HttpGet("generic")]
    public async Task<IActionResult> GetGeneric(CancellationToken ct)
    {
        try
        {
            var (content, fileName) = await _agentPackageService.BuildGenericInstallerAsync(cancellationToken: ct);

            await _loggingService.LogInfoAsync(
                LogType.Agent,
                LogSource.Api,
                "deploy.installer.generic_download",
                new { fileName, sizeBytes = content.LongLength },
                cancellationToken: ct);

            return File(content, "application/x-msdownload", fileName);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("project path does not exist", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Generic installer build failed — agent source project path not configured");
            return StatusCode(503, new { message = "Generic installer is not available on this server. Agent package build is not configured." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build generic installer");
            return StatusCode(500, new { message = "Failed to build generic installer." });
        }
    }

    private static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".exe" => "application/x-msdownload",
            ".msi" => "application/x-msi",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }
}

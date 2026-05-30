using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-updates")]
public class AgentUpdatesController(
    IAgentUpdateService agentUpdateService,
    IAgentPackageService agentPackageService,
    ISyncInvalidationPublisher syncInvalidationPublisher,
    IConfiguration configuration) : ControllerBase
{
    private static readonly HashSet<string> AllowedSyncBranches = new(StringComparer.OrdinalIgnoreCase)
    {
        "dev", "release", "beta", "lts"
    };

    [HttpGet("build/current")]
    [RequirePermission(ResourceType.Deployment, ActionType.View)]
    public async Task<IActionResult> GetCurrentBuild(
        [FromQuery] string? platform = null,
        [FromQuery] string? architecture = null,
        [FromQuery] AgentReleaseArtifactType? artifactType = null,
        CancellationToken cancellationToken = default)
    {
        var build = await agentUpdateService.GetCurrentBuildAsync(platform, architecture, artifactType, cancellationToken);
        return build is null ? NotFound(new { error = "No active build is available for the requested target." }) : Ok(build);
    }

    [HttpPost("build/refresh")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit)]
    public async Task<IActionResult> RefreshBuild(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefreshAgentBuildRequest? request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request?.ForceRebuild == true)
                await agentPackageService.PrebuildBaseBinaryAsync(forceRebuild: true, cancellationToken);

            var (content, fileName) = await agentPackageService.BuildUpdateInstallerAsync();

            var contentType = configuration["AgentPackage:InstallerContentType"]
                ?? "application/x-msdownload";

            var version = await ResolveRefreshVersionAsync(request, cancellationToken);
            var platform = string.IsNullOrWhiteSpace(request?.Platform) ? "windows" : request.Platform.Trim().ToLowerInvariant();
            var architecture = string.IsNullOrWhiteSpace(request?.Architecture) ? "amd64" : request.Architecture.Trim().ToLowerInvariant();
            var artifactType = request?.ArtifactType ?? AgentReleaseArtifactType.Installer;

            await using var stream = new MemoryStream(content, writable: false);
            var build = await agentUpdateService.RefreshCurrentBuildAsync(
                version,
                platform,
                architecture,
                artifactType,
                fileName,
                contentType,
                stream,
                signatureThumbprint: null,
                actor: HttpContext.Items["Username"] as string ?? "api",
                cancellationToken: cancellationToken);

            await syncInvalidationPublisher.PublishGlobalAsync(
                SyncResourceType.AgentUpdate,
                "agent-build-refreshed",
                cancellationToken: cancellationToken);

            return Ok(build);
        }
        catch (FileNotFoundException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task<string> ResolveRefreshVersionAsync(
        RefreshAgentBuildRequest? request,
        CancellationToken cancellationToken)
    {
        var requestedVersion = request?.Version?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            if (requestedVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                requestedVersion = requestedVersion[1..];

            if (!SemanticVersion.TryParse(requestedVersion, out _))
                throw new InvalidOperationException("version must be a valid semantic version.");

            return requestedVersion;
        }

        var current = await agentUpdateService.GetCurrentBuildAsync(
            platform: "windows",
            architecture: "amd64",
            artifactType: AgentReleaseArtifactType.Installer,
            cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(current?.Version))
            return current.Version;

        return "1.0.0";
    }

    [HttpGet("agents/{agentId:guid}/events")]
    [RequirePermission(ResourceType.Deployment, ActionType.View)]
    public async Task<IActionResult> GetAgentEvents(Guid agentId, [FromQuery] int limit = 100, CancellationToken cancellationToken = default)
    {
        var events = await agentUpdateService.GetEventsByAgentAsync(agentId, limit, cancellationToken);
        return Ok(events);
    }

    [HttpGet("dashboard/rollout")]
    [RequirePermission(ResourceType.Deployment, ActionType.View)]
    public async Task<IActionResult> GetRolloutDashboard(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var dashboard = await agentUpdateService.GetRolloutDashboardAsync(clientId, siteId, limit, cancellationToken);
        return Ok(dashboard);
    }

    [HttpPost("agents/{agentId:guid}/force-update")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit)]
    public async Task<IActionResult> ForceUpdate(Guid agentId, [FromBody] ForceAgentUpdateRequest? request, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = await agentUpdateService.TriggerForceUpdateAsync(
                agentId,
                request ?? new ForceAgentUpdateRequest(),
                HttpContext.Items["Username"] as string ?? "api",
                cancellationToken);

            return Ok(command);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Repository Sync ───────────────────────────────────────────────────

    /// <summary>
    /// Synchronizes the agent source repository with the configured branch.
    /// Allowed branches: dev, release (default), beta, lts.
    /// Executes git fetch + git reset --hard to origin/{branch}.
    /// </summary>
    [HttpPost("repository/sync")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit)]
    public async Task<IActionResult> SyncRepository(
        [FromBody] SyncAgentRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "release" : request.Branch.Trim();
        if (!AllowedSyncBranches.Contains(branch))
            return BadRequest(new { error = $"Branch '{branch}' is not allowed. Allowed: {string.Join(", ", AllowedSyncBranches.OrderBy(b => b))}." });

        try
        {
            var result = await agentPackageService.SyncRepositoryAsync(branch, cancellationToken);
            return Ok(new
            {
                result.Branch,
                BeforeCommit = result.BeforeCommit?[..Math.Min(12, result.BeforeCommit.Length)],
                AfterCommit = result.AfterCommit[..Math.Min(12, result.AfterCommit.Length)],
                result.Changed,
                result.GitMessage
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Synchronizes the agent source repository AND triggers a full rebuild
    /// (wails build + makensis) after sync. If the sync produces no changes,
    /// the rebuild is skipped unless forceRebuild is true.
    /// </summary>
    [HttpPost("repository/sync-and-build")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit)]
    public async Task<IActionResult> SyncAndBuild(
        [FromBody] SyncAgentRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "release" : request.Branch.Trim();
        if (!AllowedSyncBranches.Contains(branch))
            return BadRequest(new { error = $"Branch '{branch}' is not allowed. Allowed: {string.Join(", ", AllowedSyncBranches.OrderBy(b => b))}." });

        try
        {
            var syncResult = await agentPackageService.SyncRepositoryAsync(branch, cancellationToken);

            if (!syncResult.Changed && !request.ForceRebuild)
            {
                return Ok(new
                {
                    synced = true,
                    rebuilt = false,
                    sync = new
                    {
                        syncResult.Branch,
                        BeforeCommit = syncResult.BeforeCommit?[..Math.Min(12, syncResult.BeforeCommit.Length)],
                        AfterCommit = syncResult.AfterCommit[..Math.Min(12, syncResult.AfterCommit.Length)],
                        syncResult.Changed,
                        syncResult.GitMessage
                    },
                    message = "Repository is already up to date. Use forceRebuild=true to force a rebuild."
                });
            }

            // Force rebuild after sync
            await agentPackageService.PrebuildBaseBinaryAsync(forceRebuild: true, cancellationToken);

            var (content, fileName) = await agentPackageService.BuildUpdateInstallerAsync();

            var syncInfo = new
            {
                syncResult.Branch,
                BeforeCommit = syncResult.BeforeCommit?[..Math.Min(12, syncResult.BeforeCommit.Length)],
                AfterCommit = syncResult.AfterCommit[..Math.Min(12, syncResult.AfterCommit.Length)],
                syncResult.Changed,
                syncResult.GitMessage
            };

                return Ok(new
                {
                    synced = true,
                    rebuilt = true,
                    sync = syncInfo,
                    build = new
                    {
                        fileName,
                        sizeBytes = content.Length,
                        message = "Installer rebuilt successfully. Use POST /build/refresh to publish this build as the current self-update target."
                    }
                });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

}

public sealed record RefreshAgentBuildRequest(
    string? Version = null,
    string? Platform = null,
    string? Architecture = null,
    AgentReleaseArtifactType? ArtifactType = AgentReleaseArtifactType.Installer,
    bool ForceRebuild = false);

public sealed record SyncAgentRepositoryRequest(
    string? Branch = "release",
    bool ForceRebuild = false);

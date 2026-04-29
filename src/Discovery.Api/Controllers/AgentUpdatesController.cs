using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-updates")]
public class AgentUpdatesController(
    IAgentUpdateService agentUpdateService,
    IAgentPackageService agentPackageService,
    ISyncInvalidationPublisher syncInvalidationPublisher,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("releases")]
    [RequirePermission(ResourceType.Deployment, ActionType.View)]
    public async Task<IActionResult> ListReleases([FromQuery] bool includeInactive = false, [FromQuery] string? channel = null, CancellationToken cancellationToken = default)
    {
        var releases = await agentUpdateService.ListReleasesAsync(includeInactive, channel, cancellationToken);
        return Ok(releases);
    }

    [HttpGet("releases/{releaseId:guid}")]
    [RequirePermission(ResourceType.Deployment, ActionType.View)]
    public async Task<IActionResult> GetRelease(Guid releaseId, CancellationToken cancellationToken = default)
    {
        var release = await agentUpdateService.GetReleaseAsync(releaseId, cancellationToken);
        return release is null ? NotFound() : Ok(release);
    }

    [HttpPost("releases")]
    [RequirePermission(ResourceType.Deployment, ActionType.Create)]
    public async Task<IActionResult> CreateRelease([FromBody] AgentReleaseWriteRequest request, CancellationToken cancellationToken = default)
    {
        var release = await agentUpdateService.CreateReleaseAsync(
            request,
            HttpContext.Items["Username"] as string ?? "api",
            cancellationToken);

        await syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.AgentUpdate,
            "agent-release-created",
            cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(GetRelease), new { releaseId = release.Id }, release);
    }

    [HttpPut("releases/{releaseId:guid}")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit)]
    public async Task<IActionResult> UpdateRelease(Guid releaseId, [FromBody] AgentReleaseWriteRequest request, CancellationToken cancellationToken = default)
    {
        var release = await agentUpdateService.UpdateReleaseAsync(
            releaseId,
            request,
            HttpContext.Items["Username"] as string ?? "api",
            cancellationToken);

        await syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.AgentUpdate,
            "agent-release-updated",
            cancellationToken: cancellationToken);

        return Ok(release);
    }

    [HttpDelete("releases/{releaseId:guid}")]
    [RequirePermission(ResourceType.Deployment, ActionType.Delete)]
    public async Task<IActionResult> DeleteRelease(Guid releaseId, CancellationToken cancellationToken = default)
    {
        await agentUpdateService.DeleteReleaseAsync(releaseId, cancellationToken);
        await syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.AgentUpdate,
            "agent-release-deleted",
            cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpPost("releases/{releaseId:guid}/promote")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit)]
    public async Task<IActionResult> PromoteRelease(
        Guid releaseId,
        [FromBody] PromoteAgentReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await agentUpdateService.PromoteReleaseAsync(
                releaseId,
                request,
                HttpContext.Items["Username"] as string ?? "api",
                cancellationToken);

            await syncInvalidationPublisher.PublishGlobalAsync(
                SyncResourceType.AgentUpdate,
                "agent-release-promoted",
                cancellationToken: cancellationToken);

            return Ok(release);
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

    [HttpPost("releases/{releaseId:guid}/artifacts")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadArtifact(Guid releaseId, [FromForm] UploadAgentReleaseArtifactRequest request, CancellationToken cancellationToken = default)
    {
        if (request.File is null || request.File.Length == 0)
            return BadRequest(new { error = "Artifact file is required." });

        await using var stream = request.File.OpenReadStream();
        var artifact = await agentUpdateService.UploadArtifactAsync(
            releaseId,
            request.Platform,
            request.Architecture,
            request.ArtifactType,
            request.File.FileName,
            request.File.ContentType,
            stream,
            request.SignatureThumbprint,
            cancellationToken);

        await syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.AgentUpdate,
            "agent-release-artifact-uploaded",
            cancellationToken: cancellationToken);

        return Ok(artifact);
    }

    [HttpDelete("artifacts/{artifactId:guid}")]
    [RequirePermission(ResourceType.Deployment, ActionType.Delete)]
    public async Task<IActionResult> DeleteArtifact(Guid artifactId, CancellationToken cancellationToken = default)
    {
        await agentUpdateService.DeleteArtifactAsync(artifactId, cancellationToken);
        await syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.AgentUpdate,
            "agent-release-artifact-deleted",
            cancellationToken: cancellationToken);
        return NoContent();
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

    [HttpPost("agents/{agentId:guid}/force-check")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit)]
    public async Task<IActionResult> ForceCheck(Guid agentId, [FromBody] ForceAgentUpdateCheckRequest? request, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = await agentUpdateService.TriggerForceCheckAsync(
                agentId,
                request ?? new ForceAgentUpdateCheckRequest(),
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

    /// <summary>
    /// Builds the agent installer from the pre-compiled binary and registers it as an artifact
    /// for the specified release. Safe to call multiple times — re-uploads replace the previous artifact.
    /// </summary>
    [HttpPost("releases/{releaseId:guid}/build-artifact")]
    [RequirePermission(ResourceType.Deployment, ActionType.Edit)]
    public async Task<IActionResult> BuildArtifact(
        Guid releaseId,
        [FromBody] BuildAgentReleaseArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request.ForceRebuild)
                await agentPackageService.PrebuildBaseBinaryAsync(forceRebuild: true, cancellationToken);

            var (content, fileName) = await agentPackageService.BuildInstallerAsync(string.Empty, ResolvePublicApiBaseUrl(Request));

            var contentType = configuration["AgentPackage:InstallerContentType"]
                ?? "application/x-msdownload";

            var platform = string.IsNullOrWhiteSpace(request.Platform) ? "windows" : request.Platform.Trim().ToLowerInvariant();
            var architecture = string.IsNullOrWhiteSpace(request.Architecture) ? "amd64" : request.Architecture.Trim().ToLowerInvariant();

            using var stream = new MemoryStream(content, writable: false);
            var artifact = await agentUpdateService.UploadArtifactAsync(
                releaseId,
                platform,
                architecture,
                AgentReleaseArtifactType.Installer,
                fileName,
                contentType,
                stream,
                signatureThumbprint: null,
                cancellationToken);

            await syncInvalidationPublisher.PublishGlobalAsync(
                SyncResourceType.AgentUpdate,
                "agent-release-artifact-built",
                cancellationToken: cancellationToken);

            return Ok(artifact);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
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

    private static string ResolvePublicApiBaseUrl(HttpRequest request)
        => $"{request.Scheme}://{request.Host}{request.PathBase}/api/";
}

public sealed record UploadAgentReleaseArtifactRequest(
    string Platform,
    string Architecture,
    AgentReleaseArtifactType ArtifactType,
    IFormFile File,
    string? SignatureThumbprint);

public sealed record BuildAgentReleaseArtifactRequest(
    string? Platform,
    string? Architecture,
    bool ForceRebuild = false);

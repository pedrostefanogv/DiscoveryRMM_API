using Asp.Versioning;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints públicos (sem autenticação) para:
/// - Portal aberto /download (arquivos públicos + Zero-Touch otimizado)
/// - Links temporários de download (/download/share/:code)
/// - Links temporários de upload pelo cliente (/download/upload/:code)
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/public")]
public class PublicDownloadController(
    IManagedFileService fileService,
    IFileLinkService linkService,
    IConfigurationResolver configResolver,
    IAgentPackageService agentPackageService,
    IObjectStorageProviderFactory storageFactory,
    DiscoveryDbContext db) : ControllerBase
{
    private const string ShareSessionHeader = "X-Share-Session";

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private string? UserAgent => Request.Headers.UserAgent.ToString();
    private string? SessionToken => Request.Headers[ShareSessionHeader].FirstOrDefault();

    [HttpGet("downloads/portal")]
    public async Task<IActionResult> GetPortal(
        [FromQuery] string? search,
        [FromQuery] string? category,
        [FromQuery] string? folder = null,
        CancellationToken ct = default)
    {
        var server = await configResolver.GetServerAsync();
        var branding = await configResolver.GetConfigurationObjectAsync<BrandingSettings>("branding");

        if (!server.PublicDownloadPageEnabled)
        {
            return Ok(new PublicPortalDto(
                Enabled: false,
                Title: null,
                Description: null,
                BrandName: branding?.CompanyName ?? "Discovery RMM",
                LogoUrl: branding?.LogoUrl,
                ZeroTouch: new PublicZeroTouchInfoDto(false, false, null, null, null, null),
                Files: [],
                Categories: [],
                Folders: []));
        }

        var (files, categories, folders) = await fileService.ListPublicFilesAsync(search, category, folder, ct);

        PublicZeroTouchInfoDto zeroTouch;
        if (server.PublicDownloadZeroTouchEnabled)
        {
            var meta = await agentPackageService.GetCachedGenericInstallerMetadataAsync(ct);
            zeroTouch = new PublicZeroTouchInfoDto(
                Enabled: true,
                Available: meta is not null,
                Version: meta?.Version,
                FileName: meta?.FileName,
                SizeBytes: meta?.SizeBytes,
                Sha256: meta?.Sha256);
        }
        else
        {
            zeroTouch = new PublicZeroTouchInfoDto(false, false, null, null, null, null);
        }

        return Ok(new PublicPortalDto(
            Enabled: true,
            Title: server.PublicDownloadTitle,
            Description: server.PublicDownloadDescription,
            BrandName: branding?.CompanyName ?? "Discovery RMM",
            LogoUrl: branding?.LogoUrl,
            ZeroTouch: zeroTouch,
            Files: files,
            Categories: categories,
            Folders: folders));
    }

    [HttpPost("downloads/files/{id:guid}/download")]
    public async Task<IActionResult> DownloadPublicFile(Guid id, CancellationToken ct)
    {
        var server = await configResolver.GetServerAsync();
        if (!server.PublicDownloadPageEnabled)
            return NotFound(new { error = "Portal público de downloads está desativado." });

        try
        {
            return Ok(await fileService.GetPublicFileDownloadUrlAsync(id, ClientIp, UserAgent, ct));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("downloads/zerotouch")]
    public async Task<IActionResult> DownloadZeroTouch([FromQuery] string? mode, CancellationToken ct)
    {
        var server = await configResolver.GetServerAsync();
        if (!server.PublicDownloadPageEnabled || !server.PublicDownloadZeroTouchEnabled)
            return NotFound(new { error = "Download do agente Zero-Touch não está habilitado." });

        GenericInstallerArtifact artifact;
        try
        {
            artifact = await agentPackageService.GetGenericInstallerArtifactAsync(cancellationToken: ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("project path does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Instalador Zero-Touch não está disponível neste servidor." });
        }

        db.FileAccessLogs.Add(new FileAccessLog
        {
            Id = IdGenerator.NewId(),
            EventType = "zerotouch_download",
            ClientIp = ClientIp,
            UserAgent = UserAgent,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(artifact.S3ObjectKey))
        {
            try
            {
                var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
                var presigned = await storage.GetPresignedDownloadUrlAsync(
                    artifact.S3ObjectKey, TimeSpan.FromMinutes(15), artifact.FileName, null, ct);

                if (string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase))
                    return Ok(new PresignedDownloadResponse(presigned, artifact.FileName, artifact.SizeBytes, artifact.Sha256, 900));

                return Redirect(presigned);
            }
            catch
            {
                // Fallback direto para streaming em disco.
            }
        }

        if (string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new PresignedDownloadResponse(
                "/api/v1/public/downloads/zerotouch",
                artifact.FileName,
                artifact.SizeBytes,
                artifact.Sha256,
                900));
        }

        return PhysicalFile(artifact.LocalPath, "application/x-msdownload", artifact.FileName, enableRangeProcessing: true);
    }

    // ── Share Links (Download Temporário) ────────────────────────────────────

    [HttpGet("downloads/share/{shortCode}")]
    public async Task<IActionResult> GetShareInfo(string shortCode, CancellationToken ct)
    {
        try
        {
            return Ok(await linkService.GetPublicShareInfoAsync(shortCode, SessionToken, ct));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("downloads/share/{shortCode}/verify")]
    public async Task<IActionResult> VerifySharePasscode(
        string shortCode,
        [FromBody] VerifyPasscodeRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await linkService.VerifySharePasscodeAsync(shortCode, request.Passcode, ClientIp, UserAgent, ct);
            return result.Valid ? Ok(result) : Unauthorized(result);
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("downloads/share/{shortCode}/files/{fileId:guid}/download")]
    public async Task<IActionResult> DownloadShareFile(string shortCode, Guid fileId, CancellationToken ct)
    {
        try
        {
            return Ok(await linkService.DownloadShareFileAsync(shortCode, fileId, SessionToken, ClientIp, UserAgent, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Upload Links (File Drop) ─────────────────────────────────────────────

    [HttpGet("uploads/{shortCode}")]
    public async Task<IActionResult> GetUploadInfo(string shortCode, CancellationToken ct)
    {
        try
        {
            return Ok(await linkService.GetPublicUploadInfoAsync(shortCode, SessionToken, ct));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("uploads/{shortCode}/verify")]
    public async Task<IActionResult> VerifyUploadPasscode(
        string shortCode,
        [FromBody] VerifyPasscodeRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await linkService.VerifyUploadPasscodeAsync(shortCode, request.Passcode, ClientIp, UserAgent, ct);
            return result.Valid ? Ok(result) : Unauthorized(result);
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("uploads/{shortCode}/initiate")]
    public async Task<IActionResult> InitiateUpload(
        string shortCode,
        [FromBody] InitiatePublicUploadRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await linkService.InitiatePublicUploadAsync(shortCode, request, SessionToken, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("uploads/{shortCode}/part-url")]
    public async Task<IActionResult> GetUploadPartUrl(
        string shortCode,
        [FromQuery] string objectKey,
        [FromQuery] string uploadId,
        [FromQuery] int partNumber,
        CancellationToken ct)
    {
        try
        {
            return Ok(await linkService.GetPublicUploadPartUrlAsync(shortCode, objectKey, uploadId, partNumber, SessionToken, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("uploads/{shortCode}/complete")]
    public async Task<IActionResult> CompleteUpload(
        string shortCode,
        [FromBody] CompletePublicUploadRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await linkService.CompletePublicUploadAsync(shortCode, request, SessionToken, ClientIp, UserAgent, ct));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("uploads/{shortCode}/abort")]
    public async Task<IActionResult> AbortUpload(
        string shortCode,
        [FromQuery] string objectKey,
        [FromQuery] string uploadId,
        CancellationToken ct)
    {
        try
        {
            await linkService.AbortPublicUploadAsync(shortCode, objectKey, uploadId, SessionToken, ct);
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Meduza.Core.Configuration;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/deploy-tokens")]
public class DeployTokensController : ControllerBase
{
    private readonly IDeployTokenService _deployTokenService;
    private readonly IDeployTokenRepository _deployTokenRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IClientRepository _clientRepository;
    private readonly IAgentPackageService _agentPackageService;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly IMeshCentralProvisioningService _meshCentralProvisioningService;
    private readonly IMeshCentralApiService _meshCentralApiService;
    private readonly MeshCentralOptions _meshCentralOptions;

    public DeployTokensController(
        IDeployTokenService deployTokenService,
        IDeployTokenRepository deployTokenRepository,
        ISiteRepository siteRepository,
        IClientRepository clientRepository,
        IAgentPackageService agentPackageService,
        IConfigurationResolver configurationResolver,
        IMeshCentralProvisioningService meshCentralProvisioningService,
        IMeshCentralApiService meshCentralApiService,
        IOptions<MeshCentralOptions> meshCentralOptions)
    {
        _deployTokenService = deployTokenService;
        _deployTokenRepository = deployTokenRepository;
        _siteRepository = siteRepository;
        _clientRepository = clientRepository;
        _agentPackageService = agentPackageService;
        _configurationResolver = configurationResolver;
        _meshCentralProvisioningService = meshCentralProvisioningService;
        _meshCentralApiService = meshCentralApiService;
        _meshCentralOptions = meshCentralOptions.Value;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid clientId, [FromQuery] Guid siteId)
    {
        if (clientId == Guid.Empty || siteId == Guid.Empty)
            return BadRequest(new { error = "clientId and siteId are required." });

        var tokens = await _deployTokenRepository.GetByClientSiteAsync(clientId, siteId);
        return Ok(tokens.Select(t => new
        {
            t.Id,
            t.ClientId,
            t.SiteId,
            t.TokenPrefix,
            t.Description,
            t.ExpiresAt,
            t.CreatedAt,
            t.RevokedAt,
            t.LastUsedAt,
            t.UsedCount,
            t.MaxUses,
            t.IsRevoked,
            t.IsExpired,
            t.IsDepleted,
            t.IsValid
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDeployTokenRequest request)
    {
        var site = await _siteRepository.GetByIdAsync(request.SiteId);
        if (site is null)
            return BadRequest(new { error = "Site not found." });

        if (site.ClientId != request.ClientId)
            return BadRequest(new { error = "Site does not belong to informed client." });

        var (token, rawToken) = await _deployTokenService.CreateTokenAsync(
            request.ClientId,
            request.SiteId,
            request.Description,
            request.ExpiresInHours,
            request.MultiUse ?? false);

        var delivery = string.IsNullOrWhiteSpace(request.Delivery) ? "token" : request.Delivery.Trim().ToLowerInvariant();

        if (delivery == "installer")
        {
            try
            {
                var (installerBytes, fileName) = await _agentPackageService.BuildInstallerAsync(rawToken);
                return File(installerBytes, "application/x-msdownload", fileName);
            }
            catch (FileNotFoundException ex)
            {
                return StatusCode(503, new { error = "Installer resources are not available on this server.", detail = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(503, new { error = "Installer build failed.", detail = ex.Message });
            }
        }

        object? meshCentralInstall = null;
        var resolved = await _configurationResolver.ResolveForSiteAsync(site.Id);
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.RemoteSupportMeshCentralEnabled;
        if (meshCentralEnabledEffective)
        {
            var client = await _clientRepository.GetByIdAsync(site.ClientId);
            if (client is not null)
            {
                try
                {
                    meshCentralInstall = await _meshCentralApiService.ProvisionInstallAsync(client, site, rawToken, HttpContext.RequestAborted);
                }
                catch (InvalidOperationException)
                {
                    try
                    {
                        meshCentralInstall = _meshCentralProvisioningService.BuildInstallInstructions(
                            client,
                            site,
                            rawToken,
                            meshCentralEnabledEffective);
                    }
                    catch (InvalidOperationException)
                    {
                        // Nao bloqueia deploy token quando provisao MeshCentral estiver indisponivel.
                    }
                }
            }
        }

        return Ok(new
        {
            Token = rawToken,
            Id = token.Id,
            ClientId = token.ClientId,
            SiteId = token.SiteId,
            ExpiresAt = token.ExpiresAt,
            MaxUses = token.MaxUses,
            MeshCentral = meshCentralInstall
        });
    }

    /// <summary>
    /// Generates and returns a ZIP package containing the agent binary pre-configured
    /// with the given deploy token. The caller must supply the original rawToken
    /// (returned when the token was created) so the server can verify ownership
    /// without consuming the token.
    /// </summary>
    [HttpPost("{id:guid}/download")]
    public async Task<IActionResult> DownloadPackage(Guid id, [FromBody] DownloadPackageRequest request)
    {
        var token = await _deployTokenService.GetValidatedByIdAsync(id, request.RawToken);
        if (token is null)
            return BadRequest(new { error = "Invalid rawToken or token ID mismatch." });

        var artifact = string.IsNullOrWhiteSpace(request.Artifact) ? "portable" : request.Artifact.Trim().ToLowerInvariant();

        try
        {
            if (artifact == "installer")
            {
                var (installerBytes, fileName) = await _agentPackageService.BuildInstallerAsync(request.RawToken);
                return File(installerBytes, "application/x-msdownload", fileName);
            }

            var package = await _agentPackageService.BuildPortablePackageAsync(request.RawToken);
            return File(package, "application/zip", "meduza-discovery-setup.zip");
        }
        catch (FileNotFoundException ex)
        {
            return StatusCode(503, new { error = "Agent binary is not available on this server.", detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = "Agent package configuration is incomplete.", detail = ex.Message });
        }
    }

    /// <summary>
    /// Retorna instrucoes de instalacao MeshCentral para o token informado.
    /// Nao consome o token de deploy.
    /// </summary>
    [HttpPost("{id:guid}/meshcentral-install")]
    public async Task<IActionResult> GetMeshCentralInstall(Guid id, [FromBody] DownloadPackageRequest request)
    {
        var token = await _deployTokenService.GetValidatedByIdAsync(id, request.RawToken);
        if (token is null)
            return BadRequest(new { error = "Invalid rawToken or token ID mismatch." });

        if (!token.SiteId.HasValue || !token.ClientId.HasValue)
            return BadRequest(new { error = "Deploy token is not scoped to a valid client/site." });

        var site = await _siteRepository.GetByIdAsync(token.SiteId.Value);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        var resolved = await _configurationResolver.ResolveForSiteAsync(site.Id);
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.RemoteSupportMeshCentralEnabled;
        if (!meshCentralEnabledEffective)
            return StatusCode(403, new { error = "MeshCentral support is disabled for this scope." });

        var client = await _clientRepository.GetByIdAsync(token.ClientId.Value);
        if (client is null)
            return NotFound(new { error = "Client not found." });

        try
        {
            var instructions = await _meshCentralApiService.ProvisionInstallAsync(client, site, request.RawToken, HttpContext.RequestAborted);
            return Ok(instructions);
        }
        catch (InvalidOperationException ex)
        {
            try
            {
                var fallback = _meshCentralProvisioningService.BuildInstallInstructions(
                    client,
                    site,
                    request.RawToken,
                    meshCentralEnabledEffective);
                return Ok(fallback);
            }
            catch (InvalidOperationException)
            {
                return StatusCode(503, new { error = ex.Message });
            }
        }
    }

    /// <summary>
    /// Triggers prebuild of the Discovery base binary.
    /// Use forceRebuild=true to rebuild even when binary already exists.
    /// </summary>
    [HttpPost("prebuild")]
    public async Task<IActionResult> Prebuild([FromBody] PrebuildAgentRequest? request)
    {
        try
        {
            var force = request?.ForceRebuild ?? false;
            await _agentPackageService.PrebuildBaseBinaryAsync(force);
            return Ok(new { success = true, forceRebuild = force });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { error = "Prebuild failed.", detail = ex.Message });
        }
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        await _deployTokenService.RevokeTokenAsync(id);
        return NoContent();
    }
}

public record CreateDeployTokenRequest(Guid ClientId, Guid SiteId, string? Description, int? ExpiresInHours, bool? MultiUse, string? Delivery);
public record DownloadPackageRequest(string RawToken, string? Artifact);
public record PrebuildAgentRequest(bool ForceRebuild);

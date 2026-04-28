using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Discovery.Core.Configuration;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/deploy-tokens")]
public class DeployTokensController : ControllerBase
{
    private readonly IConfiguration _configuration;
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
        IConfiguration configuration,
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
        _configuration = configuration;
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
                var publicApiBaseUrl = ResolvePublicApiBaseUrl(Request);
                var (installerBytes, fileName) = await _agentPackageService.BuildInstallerAsync(rawToken, publicApiBaseUrl);
                return File(installerBytes, ResolveInstallerContentType(), fileName);
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
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.SupportEnabled;
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
                        meshCentralInstall = await _meshCentralProvisioningService.BuildInstallInstructionsAsync(
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
    /// Returns download options for project page installer selector.
    /// The raw token is validated without consuming usages.
    /// </summary>
    [HttpPost("installer-options")]
    public async Task<IActionResult> GetInstallerOptions([FromBody] InstallerOptionsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RawToken))
            return BadRequest(new { error = "rawToken is required." });

        var token = await _deployTokenService.GetValidatedAsync(request.RawToken);
        if (token is null)
            return Unauthorized(new { error = "Invalid, expired, revoked, or depleted deploy token." });

        return Ok(new
        {
            TokenId = token.Id,
            token.ClientId,
            token.SiteId,
            token.ExpiresAt,
            Options = new[]
            {
                new
                {
                    Type = "online",
                    DisplayName = "Online (menor)",
                    Description = "Baixa o payload completo durante a instalação.",
                    RequiresInternet = true,
                    FileExtension = ".exe",
                    Recommended = true
                },
                new
                {
                    Type = "offline",
                    DisplayName = "Offline (completo)",
                    Description = "Pacote completo para instalação sem internet.",
                    RequiresInternet = false,
                    FileExtension = ".zip",
                    Recommended = false
                }
            }
        });
    }

    /// <summary>
    /// Downloads installer package by raw deploy token, supporting
    /// online bootstrapper (smaller) and offline complete package.
    /// </summary>
    [HttpPost("download-installer")]
    public async Task<IActionResult> DownloadInstallerByToken([FromBody] DownloadInstallerByTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RawToken))
            return BadRequest(new { error = "rawToken is required." });

        var token = await _deployTokenService.GetValidatedAsync(request.RawToken);
        if (token is null)
            return Unauthorized(new { error = "Invalid, expired, revoked, or depleted deploy token." });

        var installerType = NormalizeInstallerType(request.InstallerType);
        try
        {
            if (installerType == "online")
            {
                var publicApiBaseUrl = ResolvePublicApiBaseUrl(Request);
                var (installerBytes, fileName) = await _agentPackageService.BuildInstallerAsync(request.RawToken, publicApiBaseUrl);
                return File(installerBytes, ResolveInstallerContentType(), fileName);
            }

            var package = await _agentPackageService.BuildPortablePackageAsync(request.RawToken, ResolvePublicApiBaseUrl(Request));
            return File(package, "application/zip", "discovery-discovery-offline.zip");
        }
        catch (FileNotFoundException ex)
        {
            return StatusCode(503, new { error = "Installer resources are not available on this server.", detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = "Installer package configuration is incomplete.", detail = ex.Message });
        }
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
                var publicApiBaseUrl = ResolvePublicApiBaseUrl(Request);
                var (installerBytes, fileName) = await _agentPackageService.BuildInstallerAsync(request.RawToken, publicApiBaseUrl);
                return File(installerBytes, ResolveInstallerContentType(), fileName);
            }

            var package = await _agentPackageService.BuildPortablePackageAsync(request.RawToken, ResolvePublicApiBaseUrl(Request));
            return File(package, "application/zip", "discovery-discovery-setup.zip");
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
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.SupportEnabled;
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
                var fallback = await _meshCentralProvisioningService.BuildInstallInstructionsAsync(
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

    private string ResolveInstallerContentType()
    {
        var profile = ResolveActiveProfile();
        var profileValue = _configuration[$"AgentPackage:Profiles:{profile}:InstallerContentType"];
        if (!string.IsNullOrWhiteSpace(profileValue))
            return profileValue;

        return _configuration["AgentPackage:InstallerContentType"] ?? "application/x-msdownload";
    }

    private string ResolveActiveProfile()
    {
        var configured = _configuration["AgentPackage:ActiveProfile"];
        if (string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsWindows() ? "windows" : "linux";

        return configured.Trim().ToLowerInvariant();
    }

    private static string NormalizeInstallerType(string? installerType)
    {
        var normalized = string.IsNullOrWhiteSpace(installerType)
            ? "online"
            : installerType.Trim().ToLowerInvariant();

        return normalized switch
        {
            "online" or "installer" => "online",
            "offline" or "portable" => "offline",
            _ => "online"
        };
    }

    private static string ResolvePublicApiBaseUrl(HttpRequest request)
        => $"{request.Scheme}://{request.Host}{request.PathBase}/api/";
}

public record CreateDeployTokenRequest(Guid ClientId, Guid SiteId, string? Description, int? ExpiresInHours, bool? MultiUse, string? Delivery);
public record DownloadPackageRequest(string RawToken, string? Artifact);
public record InstallerOptionsRequest(string RawToken);
public record DownloadInstallerByTokenRequest(string RawToken, string? InstallerType);
public record PrebuildAgentRequest(bool ForceRebuild);

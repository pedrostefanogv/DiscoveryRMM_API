using Discovery.Core.Interfaces;
using Discovery.Core.Helpers;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Configuration;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/meshcentral")]
[RequireUserAuth]
public class MeshCentralController : ControllerBase
{
    private readonly IUserRepository _userRepository;
    private readonly IClientRepository _clientRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly IMeshCentralApiService _meshCentralApiService;
    private readonly IMeshCentralEmbeddingService _meshCentralEmbeddingService;
    private readonly IMeshCentralIdentitySyncService _meshCentralIdentitySyncService;
    private readonly IMeshCentralNodeLinkBackfillService _meshCentralNodeLinkBackfillService;
    private readonly IMeshCentralGroupPolicySyncService _meshCentralGroupPolicySyncService;
    private readonly IMeshCentralRightsProfileRepository _meshCentralRightsProfileRepository;
    private readonly IRoleRepository _roleRepository;
    private readonly MeshCentralOptions _meshCentralOptions;

    public MeshCentralController(
        IUserRepository userRepository,
        IClientRepository clientRepository,
        ISiteRepository siteRepository,
        IAgentRepository agentRepository,
        IConfigurationResolver configurationResolver,
        IMeshCentralApiService meshCentralApiService,
        IMeshCentralEmbeddingService meshCentralEmbeddingService,
        IMeshCentralIdentitySyncService meshCentralIdentitySyncService,
        IMeshCentralNodeLinkBackfillService meshCentralNodeLinkBackfillService,
        IMeshCentralGroupPolicySyncService meshCentralGroupPolicySyncService,
        IMeshCentralRightsProfileRepository meshCentralRightsProfileRepository,
        IRoleRepository roleRepository,
        IOptions<MeshCentralOptions> meshCentralOptions)
    {
        _userRepository = userRepository;
        _clientRepository = clientRepository;
        _siteRepository = siteRepository;
        _agentRepository = agentRepository;
        _configurationResolver = configurationResolver;
        _meshCentralApiService = meshCentralApiService;
        _meshCentralEmbeddingService = meshCentralEmbeddingService;
        _meshCentralIdentitySyncService = meshCentralIdentitySyncService;
        _meshCentralNodeLinkBackfillService = meshCentralNodeLinkBackfillService;
        _meshCentralGroupPolicySyncService = meshCentralGroupPolicySyncService;
        _meshCentralRightsProfileRepository = meshCentralRightsProfileRepository;
        _roleRepository = roleRepository;
        _meshCentralOptions = meshCentralOptions.Value;
    }

    [HttpGet("rights-profiles")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetRightsProfiles()
    {
        var profiles = await _meshCentralRightsProfileRepository.GetAllAsync();
        return Ok(profiles.Select(p => new MeshCentralRightsProfileDto(
            p.Id,
            p.Name,
            p.Description,
            p.RightsMask,
            p.IsSystem,
            p.CreatedAt,
            p.UpdatedAt)));
    }

    [HttpPost("rights-profiles")]
    [RequirePermission(ResourceType.Users, ActionType.Create)]
    public async Task<IActionResult> CreateRightsProfile([FromBody] CreateMeshCentralRightsProfileRequest request)
    {
        var normalizedName = NormalizeProfileName(request.Name);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return BadRequest(new { error = "Nome do perfil ├® obrigat├│rio." });

        if (await _meshCentralRightsProfileRepository.IsNameInUseAsync(normalizedName))
            return Conflict(new { error = "J├í existe um perfil com este nome." });

        var profile = new MeshCentralRightsProfile
        {
            Id = IdGenerator.NewId(),
            Name = normalizedName,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            RightsMask = request.RightsMask,
            IsSystem = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _meshCentralRightsProfileRepository.CreateAsync(profile);

        return CreatedAtAction(
            nameof(GetRightsProfiles),
            null,
            new MeshCentralRightsProfileDto(
                profile.Id,
                profile.Name,
                profile.Description,
                profile.RightsMask,
                profile.IsSystem,
                profile.CreatedAt,
                profile.UpdatedAt));
    }

    [HttpPut("rights-profiles/{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> UpdateRightsProfile(Guid id, [FromBody] UpdateMeshCentralRightsProfileRequest request)
    {
        var profile = await _meshCentralRightsProfileRepository.GetByIdAsync(id);
        if (profile is null)
            return NotFound(new { error = "Perfil n├úo encontrado." });

        if (request.Name is not null)
        {
            var normalizedName = NormalizeProfileName(request.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
                return BadRequest(new { error = "Nome do perfil n├úo pode ser vazio." });

            if (await _meshCentralRightsProfileRepository.IsNameInUseAsync(normalizedName, id))
                return Conflict(new { error = "J├í existe um perfil com este nome." });

            profile.Name = normalizedName;
        }

        if (request.Description is not null)
            profile.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        if (request.RightsMask.HasValue)
            profile.RightsMask = request.RightsMask.Value;

        profile.UpdatedAt = DateTime.UtcNow;
        await _meshCentralRightsProfileRepository.UpdateAsync(profile);

        return Ok(new MeshCentralRightsProfileDto(
            profile.Id,
            profile.Name,
            profile.Description,
            profile.RightsMask,
            profile.IsSystem,
            profile.CreatedAt,
            profile.UpdatedAt));
    }

    [HttpDelete("rights-profiles/{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Delete)]
    public async Task<IActionResult> DeleteRightsProfile(Guid id)
    {
        var profile = await _meshCentralRightsProfileRepository.GetByIdAsync(id);
        if (profile is null)
            return NotFound(new { error = "Perfil n├úo encontrado." });

        if (profile.IsSystem)
            return BadRequest(new { error = "Perfis de sistema n├úo podem ser removidos." });

        if (await _meshCentralRightsProfileRepository.IsProfileReferencedByRolesAsync(profile.Name))
            return BadRequest(new { error = "Perfil est├í vinculado a uma ou mais roles e n├úo pode ser removido." });

        await _meshCentralRightsProfileRepository.DeleteAsync(id);
        return NoContent();
    }

    [HttpGet("rights-profiles/usage")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetRightsProfilesUsage()
    {
        var roles = await _roleRepository.GetAllAsync();
        var usage = roles
            .Where(r => !string.IsNullOrWhiteSpace(r.MeshRightsProfile))
            .GroupBy(r => r.MeshRightsProfile!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MeshCentralRightsProfileUsageDto(g.Key, g.Count(), g.Select(r => r.Name).OrderBy(n => n).ToArray()))
            .OrderBy(item => item.ProfileName)
            .ToArray();

        return Ok(usage);
    }

    /// <summary>
    /// Gera URL de embedding para o usuario autenticado da sessao.
    /// O username usado no token SEMPRE vem do vinculo MeshCentral do usuario logado.
    /// </summary>
    [HttpPost("embed-url")]
    public async Task<IActionResult> CreateUserEmbedUrl([FromBody] MeshCentralUserEmbedRequest request)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var authenticatedUser = await _userRepository.GetByIdAsync(userId);
        if (authenticatedUser is null)
            return Unauthorized(new { error = "Authenticated user not found." });

        if (string.IsNullOrWhiteSpace(authenticatedUser.MeshCentralUsername))
            return BadRequest(new { error = "Authenticated user is not linked to a MeshCentral username." });

        var meshUsername = authenticatedUser.MeshCentralUsername.Trim();

        var client = await _clientRepository.GetByIdAsync(request.ClientId);
        if (client is null)
            return NotFound(new { error = "Client not found." });

        var site = await _siteRepository.GetByIdAsync(request.SiteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        if (site.ClientId != request.ClientId)
            return BadRequest(new { error = "Site does not belong to informed client." });

        Guid? validatedAgentId = null;
        string? validatedAgentMeshNodeId = null;
        if (request.AgentId.HasValue)
        {
            var agent = await _agentRepository.GetByIdAsync(request.AgentId.Value);
            if (agent is null)
                return NotFound(new { error = "Agent not found." });

            if (agent.SiteId != request.SiteId)
                return BadRequest(new { error = "Agent does not belong to informed site." });

            validatedAgentId = agent.Id;
            validatedAgentMeshNodeId = agent.MeshCentralNodeId;
        }

        var resolved = await _configurationResolver.ResolveForSiteAsync(request.SiteId);
        var meshCentralEnabledEffective = _meshCentralOptions.Enabled && resolved.SupportEnabled;
        if (!meshCentralEnabledEffective)
            return StatusCode(403, new { error = "MeshCentral support is disabled for this scope." });

        var viewMode = request.ViewMode ?? 11;
        var effectiveMeshNodeId = string.IsNullOrWhiteSpace(validatedAgentMeshNodeId)
            ? request.MeshNodeId
            : validatedAgentMeshNodeId;
        try
        {
            var embed = await _meshCentralEmbeddingService.GenerateUserEmbedUrlAsync(
                meshUsername,
                request.ClientId,
                request.SiteId,
                validatedAgentId,
                viewMode,
                request.HideMask,
                effectiveMeshNodeId,
                request.GotoDeviceName,
                HttpContext.RequestAborted);

            return Ok(new
            {
                url = embed.Url,
                expiresAtUtc = embed.ExpiresAtUtc,
                viewMode = embed.ViewMode,
                hideMask = embed.HideMask,
                clientId = request.ClientId,
                siteId = request.SiteId,
                agentId = validatedAgentId,
                meshNodeId = effectiveMeshNodeId,
                meshUsername
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Executa backfill/reconcilia├º├úo de usu├írios Discovery para MeshCentral.
    /// applyChanges=false executa dry-run sem persistir altera├º├Áes remotas.
    /// </summary>
    [HttpPost("identity-sync/backfill")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> RunIdentitySyncBackfill([FromBody] MeshCentralIdentityBackfillRequest request)
    {
        var report = await _meshCentralIdentitySyncService.RunBackfillAsync(
            request.ClientId,
            request.SiteId,
            request.ApplyChanges,
            HttpContext.RequestAborted);

        return Ok(report);
    }

    [HttpPost("node-links/backfill")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> RunNodeLinkBackfill([FromBody] MeshCentralNodeLinkBackfillRequest request)
    {
        var report = await _meshCentralNodeLinkBackfillService.RunBackfillAsync(
            request.ClientId,
            request.SiteId,
            request.ApplyChanges,
            HttpContext.RequestAborted);

        return Ok(report);
    }

    [HttpGet("diagnostics/health")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.View)]
    public async Task<IActionResult> GetHealth([FromQuery] Guid? siteId = null, [FromQuery] Guid? agentId = null)
    {
        Site? site = null;
        if (siteId.HasValue)
        {
            site = await _siteRepository.GetByIdAsync(siteId.Value);
            if (site is null)
                return NotFound(new { error = "Site not found." });
        }

        Agent? agent = null;
        if (agentId.HasValue)
        {
            agent = await _agentRepository.GetByIdAsync(agentId.Value);
            if (agent is null)
                return NotFound(new { error = "Agent not found." });

            if (site is not null && agent.SiteId != site.Id)
                return BadRequest(new { error = "Agent does not belong to informed site." });
        }

        try
        {
            var health = await _meshCentralApiService.RunHealthCheckAsync(HttpContext.RequestAborted);
            var siteStatus = site is null
                ? null
                : await _meshCentralGroupPolicySyncService.GetSiteStatusAsync(site.Id, HttpContext.RequestAborted);

            return Ok(new
            {
                health,
                site = siteStatus,
                agent = agent is null
                    ? null
                    : new
                    {
                        agent.Id,
                        agent.SiteId,
                        agent.Hostname,
                        agent.MeshCentralNodeId
                    }
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Consulta o status da policy efetiva do grupo MeshCentral por site (snapshot x efetivo).
    /// </summary>
    [HttpGet("group-policy/sites/{siteId:guid}/status")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.View)]
    public async Task<IActionResult> GetGroupPolicyStatus(Guid siteId)
    {
        try
        {
            var status = await _meshCentralGroupPolicySyncService.GetSiteStatusAsync(siteId, HttpContext.RequestAborted);
            return Ok(status);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reconcilia policy de grupos MeshCentral por escopo (global/client/site).
    /// applyChanges=false executa dry-run sem aplicar alteracoes remotas.
    /// </summary>
    [HttpPost("group-policy/reconcile")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.Edit)]
    public async Task<IActionResult> ReconcileGroupPolicy([FromBody] MeshCentralGroupPolicyReconcileRequest request)
    {
        var report = await _meshCentralGroupPolicySyncService.RunBackfillAsync(
            request.ClientId,
            request.SiteId,
            request.ApplyChanges,
            HttpContext.RequestAborted);

        return Ok(report);
    }

    private static string NormalizeProfileName(string? name)
        => string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToLowerInvariant();
}

public record MeshCentralUserEmbedRequest(
    Guid ClientId,
    Guid SiteId,
    Guid? AgentId = null,
    int? ViewMode = null,
    int? HideMask = null,
    string? MeshNodeId = null,
    string? GotoDeviceName = null);

public record MeshCentralIdentityBackfillRequest(
    bool ApplyChanges = false,
    Guid? ClientId = null,
    Guid? SiteId = null);

public record MeshCentralNodeLinkBackfillRequest(
    bool ApplyChanges = false,
    Guid? ClientId = null,
    Guid? SiteId = null);

public record MeshCentralGroupPolicyReconcileRequest(
    bool ApplyChanges = false,
    Guid? ClientId = null,
    Guid? SiteId = null);

public record MeshCentralRightsProfileDto(
    Guid Id,
    string Name,
    string? Description,
    int RightsMask,
    bool IsSystem,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record CreateMeshCentralRightsProfileRequest(
    string Name,
    int RightsMask,
    string? Description = null);

public record UpdateMeshCentralRightsProfileRequest(
    string? Name = null,
    int? RightsMask = null,
    string? Description = null);

public record MeshCentralRightsProfileUsageDto(
    string ProfileName,
    int RolesCount,
    string[] Roles);

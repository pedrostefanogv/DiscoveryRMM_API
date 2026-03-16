using Meduza.Core.Interfaces;
using Meduza.Core.Helpers;
using Meduza.Core.Entities;
using Meduza.Core.Interfaces.Identity;
using Meduza.Core.Enums.Identity;
using Meduza.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/meshcentral")]
[RequireUserAuth]
public class MeshCentralController : ControllerBase
{
    private readonly IClientRepository _clientRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly IMeshCentralEmbeddingService _meshCentralEmbeddingService;
    private readonly IMeshCentralIdentitySyncService _meshCentralIdentitySyncService;
    private readonly IMeshCentralGroupPolicySyncService _meshCentralGroupPolicySyncService;
    private readonly IMeshCentralRightsProfileRepository _meshCentralRightsProfileRepository;
    private readonly IRoleRepository _roleRepository;

    public MeshCentralController(
        IClientRepository clientRepository,
        ISiteRepository siteRepository,
        IAgentRepository agentRepository,
        IConfigurationResolver configurationResolver,
        IMeshCentralEmbeddingService meshCentralEmbeddingService,
        IMeshCentralIdentitySyncService meshCentralIdentitySyncService,
        IMeshCentralGroupPolicySyncService meshCentralGroupPolicySyncService,
        IMeshCentralRightsProfileRepository meshCentralRightsProfileRepository,
        IRoleRepository roleRepository)
    {
        _clientRepository = clientRepository;
        _siteRepository = siteRepository;
        _agentRepository = agentRepository;
        _configurationResolver = configurationResolver;
        _meshCentralEmbeddingService = meshCentralEmbeddingService;
        _meshCentralIdentitySyncService = meshCentralIdentitySyncService;
        _meshCentralGroupPolicySyncService = meshCentralGroupPolicySyncService;
        _meshCentralRightsProfileRepository = meshCentralRightsProfileRepository;
        _roleRepository = roleRepository;
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
            return BadRequest(new { error = "Nome do perfil é obrigatório." });

        if (await _meshCentralRightsProfileRepository.IsNameInUseAsync(normalizedName))
            return Conflict(new { error = "Já existe um perfil com este nome." });

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
            return NotFound(new { error = "Perfil não encontrado." });

        if (request.Name is not null)
        {
            var normalizedName = NormalizeProfileName(request.Name);
            if (string.IsNullOrWhiteSpace(normalizedName))
                return BadRequest(new { error = "Nome do perfil não pode ser vazio." });

            if (await _meshCentralRightsProfileRepository.IsNameInUseAsync(normalizedName, id))
                return Conflict(new { error = "Já existe um perfil com este nome." });

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
            return NotFound(new { error = "Perfil não encontrado." });

        if (profile.IsSystem)
            return BadRequest(new { error = "Perfis de sistema não podem ser removidos." });

        if (await _meshCentralRightsProfileRepository.IsProfileReferencedByRolesAsync(profile.Name))
            return BadRequest(new { error = "Perfil está vinculado a uma ou mais roles e não pode ser removido." });

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
    /// Endpoint de preparacao para autenticacao unificada: gera URL de embedding para usuario MeshCentral informado.
    /// </summary>
    [HttpPost("embed-url")]
    public async Task<IActionResult> CreateUserEmbedUrl([FromBody] MeshCentralUserEmbedRequest request)
    {
        var client = await _clientRepository.GetByIdAsync(request.ClientId);
        if (client is null)
            return NotFound(new { error = "Client not found." });

        var site = await _siteRepository.GetByIdAsync(request.SiteId);
        if (site is null)
            return NotFound(new { error = "Site not found." });

        if (site.ClientId != request.ClientId)
            return BadRequest(new { error = "Site does not belong to informed client." });

        Guid? validatedAgentId = null;
        if (request.AgentId.HasValue)
        {
            var agent = await _agentRepository.GetByIdAsync(request.AgentId.Value);
            if (agent is null)
                return NotFound(new { error = "Agent not found." });

            if (agent.SiteId != request.SiteId)
                return BadRequest(new { error = "Agent does not belong to informed site." });

            validatedAgentId = agent.Id;
        }

        var resolved = await _configurationResolver.ResolveForSiteAsync(request.SiteId);
        if (!resolved.RemoteSupportMeshCentralEnabled)
            return StatusCode(403, new { error = "MeshCentral support is disabled for this scope." });

        var viewMode = request.ViewMode ?? 11;
        try
        {
            var embed = await _meshCentralEmbeddingService.GenerateUserEmbedUrlAsync(
                request.MeshUsername,
                request.ClientId,
                request.SiteId,
                validatedAgentId,
                viewMode,
                request.HideMask,
                request.MeshNodeId,
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
                meshUsername = request.MeshUsername
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Executa backfill/reconciliação de usuários Meduza para MeshCentral.
    /// applyChanges=false executa dry-run sem persistir alterações remotas.
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
    string MeshUsername,
    Guid? AgentId = null,
    int? ViewMode = null,
    int? HideMask = null,
    string? MeshNodeId = null,
    string? GotoDeviceName = null);

public record MeshCentralIdentityBackfillRequest(
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

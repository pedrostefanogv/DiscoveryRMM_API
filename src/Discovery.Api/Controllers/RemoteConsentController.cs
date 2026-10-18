using Discovery.Api.Filters;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Controllers;

/// <summary>
/// Administração da política de consentimento de acesso remoto: overrides por
/// usuário/grupo e simulação da política efetiva para um agente+usuário.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/remote-consent")]
public class RemoteConsentController(
    DiscoveryDbContext db,
    IRemoteConsentPolicyResolver resolver,
    IAgentRepository agentRepo,
    IUserRepository userRepo,
    IUserGroupRepository groupRepo,
    IClientRepository clientRepo,
    ISiteRepository siteRepo,
    IConfigurationAuditService audit,
    ILogger<RemoteConsentController> logger) : ControllerBase
{
    private string? CurrentUser => HttpContext.Items["Username"] as string;

    [HttpGet("effective")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> GetEffective(
        [FromQuery] Guid agentId,
        [FromQuery] Guid? userId,
        CancellationToken ct = default)
    {
        if (agentId == Guid.Empty)
            return BadRequest(new { error = "agentId é obrigatório." });

        var agent = await agentRepo.GetByIdAsync(agentId);
        if (agent is null) return NotFound(new { error = "Agent not found." });

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return NotFound(new { error = "Site not found." });

        var policy = await resolver.ResolveAsync(
            userId ?? Guid.Empty, site.ClientId, agent.SiteId, ct);

        return Ok(new
        {
            agentId,
            userId,
            clientId = site.ClientId,
            siteId = agent.SiteId,
            mode = policy.Mode.ToString(),
            timeoutSeconds = policy.TimeoutSeconds,
            timeoutAction = policy.TimeoutAction.ToString(),
            showBanner = policy.ShowBanner,
            message = policy.Message,
            bannerText = policy.BannerText,
            topSource = policy.TopSource(),
            sources = policy.Sources.ToDictionary(
                item => item.Key,
                item => new { source = item.Value.Source, scopeId = item.Value.ScopeId, subjectName = item.Value.SubjectName })
        });
    }

    [HttpGet("overrides")]
    [RequirePermission(ResourceType.Users, ActionType.View)]
    public async Task<IActionResult> ListOverrides(
        [FromQuery] RemoteConsentSubjectType? subjectType,
        [FromQuery] Guid? subjectId,
        [FromQuery] Guid? clientId,
        [FromQuery] Guid? siteId,
        CancellationToken ct = default)
    {
        var query = db.RemoteConsentPolicyOverrides.AsNoTracking().AsQueryable();
        if (subjectType.HasValue) query = query.Where(item => item.SubjectType == subjectType.Value);
        if (subjectId.HasValue) query = query.Where(item => item.SubjectId == subjectId.Value);
        if (clientId.HasValue) query = query.Where(item => item.ClientId == clientId.Value || item.ClientId == Guid.Empty);
        if (siteId.HasValue) query = query.Where(item => item.SiteId == siteId.Value || item.SiteId == Guid.Empty);

        var items = await query.OrderBy(item => item.SubjectType).ThenBy(item => item.CreatedAt).ToListAsync(ct);
        return Ok(items.Select(ToDto));
    }

    [HttpPost("overrides")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> CreateOverride([FromBody] RemoteConsentOverrideRequest request, CancellationToken ct = default)
    {
        var validation = await ValidateAsync(request, null, ct);
        if (validation is not null) return BadRequest(new { error = validation });

        var entity = new RemoteConsentPolicyOverride
        {
            Id = Guid.NewGuid(),
            SubjectType = request.SubjectType,
            SubjectId = request.SubjectId,
            ClientId = request.ClientId ?? Guid.Empty,
            SiteId = request.SiteId ?? Guid.Empty,
            Mode = request.Mode,
            TimeoutSeconds = request.TimeoutSeconds,
            TimeoutAction = request.TimeoutAction,
            ShowBanner = request.ShowBanner,
            Message = Trim(request.Message),
            BannerText = Trim(request.BannerText),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = CurrentUser
        };

        db.RemoteConsentPolicyOverrides.Add(entity);
        await db.SaveChangesAsync(ct);
        await AuditAsync(entity.Id, "created", null, Describe(entity), ct);

        return Created($"/api/v1/remote-consent/overrides/{entity.Id}", ToDto(entity));
    }

    [HttpPut("overrides/{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> UpdateOverride(Guid id, [FromBody] RemoteConsentOverrideRequest request, CancellationToken ct = default)
    {
        var entity = await db.RemoteConsentPolicyOverrides.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null) return NotFound(new { error = "Override not found." });

        var validation = await ValidateAsync(request, id, ct);
        if (validation is not null) return BadRequest(new { error = validation });

        var before = Describe(entity);
        entity.SubjectType = request.SubjectType;
        entity.SubjectId = request.SubjectId;
        entity.ClientId = request.ClientId ?? Guid.Empty;
        entity.SiteId = request.SiteId ?? Guid.Empty;
        entity.Mode = request.Mode;
        entity.TimeoutSeconds = request.TimeoutSeconds;
        entity.TimeoutAction = request.TimeoutAction;
        entity.ShowBanner = request.ShowBanner;
        entity.Message = Trim(request.Message);
        entity.BannerText = Trim(request.BannerText);
        entity.UpdatedAt = DateTime.UtcNow;
        entity.UpdatedBy = CurrentUser;

        await db.SaveChangesAsync(ct);
        await AuditAsync(entity.Id, "updated", before, Describe(entity), ct);
        return Ok(ToDto(entity));
    }

    [HttpDelete("overrides/{id:guid}")]
    [RequirePermission(ResourceType.Users, ActionType.Edit)]
    public async Task<IActionResult> DeleteOverride(Guid id, CancellationToken ct = default)
    {
        var entity = await db.RemoteConsentPolicyOverrides.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null) return NotFound(new { error = "Override not found." });

        db.RemoteConsentPolicyOverrides.Remove(entity);
        await db.SaveChangesAsync(ct);
        await AuditAsync(entity.Id, "deleted", Describe(entity), null, ct);
        return NoContent();
    }

    private async Task<string?> ValidateAsync(
        RemoteConsentOverrideRequest request,
        Guid? currentId,
        CancellationToken ct)
    {
        if (request.SubjectId == Guid.Empty) return "subjectId é obrigatório.";
        if (!Enum.IsDefined(request.SubjectType)) return "subjectType inválido.";

        if (request.SubjectType == RemoteConsentSubjectType.User)
        {
            var user = await userRepo.GetByIdAsync(request.SubjectId);
            if (user is null) return "Usuário não encontrado.";
        }
        else
        {
            var group = await groupRepo.GetByIdAsync(request.SubjectId);
            if (group is null) return "Grupo não encontrado.";
        }

        if (!HasAnyValue(request)) return "Informe ao menos um campo de política (os demais herdam).";
        if (request.Mode.HasValue && !Enum.IsDefined(request.Mode.Value)) return "mode inválido.";
        if (request.TimeoutAction.HasValue && !Enum.IsDefined(request.TimeoutAction.Value)) return "timeoutAction inválido.";
        if (request.TimeoutSeconds is < 10 or > 600) return "timeoutSeconds deve ficar entre 10 e 600.";
        if (request.Message?.Length > 500) return "message não pode exceder 500 caracteres.";
        if (request.BannerText?.Length > 500) return "bannerText não pode exceder 500 caracteres.";

        var clientId = request.ClientId ?? Guid.Empty;
        var siteId = request.SiteId ?? Guid.Empty;
        if (siteId != Guid.Empty)
        {
            var site = await siteRepo.GetByIdAsync(siteId);
            if (site is null) return "Site não encontrado.";
            if (clientId != Guid.Empty && site.ClientId != clientId)
                return "O site informado não pertence ao cliente informado.";
        }
        else if (clientId != Guid.Empty)
        {
            var client = await clientRepo.GetByIdAsync(clientId);
            if (client is null) return "Cliente não encontrado.";
        }

        var duplicate = await db.RemoteConsentPolicyOverrides.AsNoTracking().AnyAsync(item =>
            item.SubjectType == request.SubjectType
            && item.SubjectId == request.SubjectId
            && item.ClientId == clientId
            && item.SiteId == siteId
            && (!currentId.HasValue || item.Id != currentId.Value), ct);
        if (duplicate) return "Já existe um override para este sujeito/escopo.";

        return null;
    }

    private static bool HasAnyValue(RemoteConsentOverrideRequest request) =>
        request.Mode.HasValue
        || request.TimeoutSeconds.HasValue
        || request.TimeoutAction.HasValue
        || request.ShowBanner.HasValue
        || !string.IsNullOrWhiteSpace(request.Message)
        || !string.IsNullOrWhiteSpace(request.BannerText);

    private async Task AuditAsync(Guid id, string action, string? before, string? after, CancellationToken ct)
    {
        try
        {
            await audit.LogChangeAsync("RemoteConsentPolicy", id, action, before, after, null, CurrentUser);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao auditar override de consentimento {OverrideId}", id);
        }
    }

    private static string Describe(RemoteConsentPolicyOverride item) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            subjectType = item.SubjectType.ToString(),
            item.SubjectId,
            item.ClientId,
            item.SiteId,
            mode = item.Mode?.ToString(),
            item.TimeoutSeconds,
            timeoutAction = item.TimeoutAction?.ToString(),
            item.ShowBanner,
            item.Message,
            item.BannerText
        });

    private static object ToDto(RemoteConsentPolicyOverride item) => new
    {
        id = item.Id,
        subjectType = item.SubjectType.ToString(),
        item.SubjectId,
        clientId = item.ClientId == Guid.Empty ? (Guid?)null : item.ClientId,
        siteId = item.SiteId == Guid.Empty ? (Guid?)null : item.SiteId,
        mode = item.Mode?.ToString(),
        item.TimeoutSeconds,
        timeoutAction = item.TimeoutAction?.ToString(),
        item.ShowBanner,
        item.Message,
        item.BannerText,
        item.CreatedAt,
        item.UpdatedAt,
        item.UpdatedBy
    };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record RemoteConsentOverrideRequest(
    RemoteConsentSubjectType SubjectType,
    Guid SubjectId,
    Guid? ClientId = null,
    Guid? SiteId = null,
    RemoteConsentMode? Mode = null,
    int? TimeoutSeconds = null,
    RemoteConsentTimeoutAction? TimeoutAction = null,
    bool? ShowBanner = null,
    string? Message = null,
    string? BannerText = null);

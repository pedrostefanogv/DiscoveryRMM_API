using System.Text.Json;
using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/configurations")]
public class ConfigurationsController : ControllerBase
{
    private readonly IConfigurationService _config;
    private readonly IAiModelCatalogService _aiCatalog;
    private readonly IAiProviderCredentialRepository _aiCredentialRepo;
    private readonly IObjectStorageProviderFactory _objectStorageFactory;
    private readonly INatsConnectionValidator _natsValidator;

    public ConfigurationsController(
        IConfigurationService configService,
        IAiModelCatalogService aiCatalogService,
        IAiProviderCredentialRepository aiCredentialRepo,
        IObjectStorageProviderFactory objectStorageFactory,
        INatsConnectionValidator natsValidator)
    {
        _config = configService;
        _aiCatalog = aiCatalogService;
        _aiCredentialRepo = aiCredentialRepo;
        _objectStorageFactory = objectStorageFactory;
        _natsValidator = natsValidator;
    }

    private string? CurrentUser => HttpContext.Items["Username"] as string;

    // ── Server ──────────────────────────────────────────────────────────

    [HttpGet("server")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetServer() => Ok(await _config.GetServerConfigAsync());

    [HttpGet("server/metadata")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public IActionResult GetServerMetadata() => Ok(new { fields = new[] { "AgentOnlineGraceSeconds", "NatsEnabled", "NatsServerHostInternal" } });

    [HttpPut("server")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateServer([FromBody] ServerConfiguration config) => Ok(await _config.UpdateServerAsync(config, CurrentUser));

    [HttpPatch("server")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchServer([FromBody] Dictionary<string, object> updates) => Ok(await _config.PatchServerAsync(updates, CurrentUser));

    [HttpPost("server/reset")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> ResetServer() => Ok(await _config.ResetServerAsync(CurrentUser));

    [HttpGet("server/reporting")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetServerReporting()
    {
        var c = await _config.GetServerConfigAsync();
        return Ok(string.IsNullOrWhiteSpace(c.ReportingSettingsJson) ? new { } : JsonSerializer.Deserialize<object>(c.ReportingSettingsJson));
    }

    [HttpPut("server/reporting")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateServerReporting([FromBody] object reporting) => Ok(await _config.PatchServerAsync(new Dictionary<string, object> { ["ReportingSettingsJson"] = JsonSerializer.Serialize(reporting) }, CurrentUser));

    [HttpPost("server/object-storage/test")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Execute)]
    public async Task<IActionResult> TestObjectStorage(CancellationToken ct) => Ok(await _objectStorageFactory.TestConnectionAsync(ct));

    [HttpPost("server/nats/test")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Execute)]
    public async Task<IActionResult> TestNats([FromBody] NatsConnectionTestRequest req, CancellationToken ct)
    {
        var (ok, errors) = await _natsValidator.ValidateConnectionAsync(req.Url, req.User, req.Password, ct);
        return Ok(new { ok, errors });
    }

    [HttpPatch("server/nats")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchNats([FromBody] Dictionary<string, object> updates) => Ok(await _config.PatchServerAsync(updates, CurrentUser));

    // ── Clients ────────────────────────────────────────────────────────

    [HttpGet("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.View)]
    public async Task<IActionResult> GetClient(Guid clientId)
    {
        var c = await _config.GetClientConfigAsync(clientId);
        return c is null ? NotFound() : Ok(c);
    }

    [HttpPut("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateClient(Guid clientId, [FromBody] ClientConfiguration config) => Ok(await _config.UpdateClientAsync(clientId, config, CurrentUser));

    [HttpPatch("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchClient(Guid clientId, [FromBody] Dictionary<string, object> updates) => Ok(await _config.PatchClientAsync(clientId, updates, CurrentUser));

    [HttpDelete("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.Delete)]
    public async Task<IActionResult> DeleteClient(Guid clientId) { await _config.DeleteClientConfigAsync(clientId); return NoContent(); }

    // ── Sites ──────────────────────────────────────────────────────────

    [HttpGet("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.View)]
    public async Task<IActionResult> GetSite(Guid siteId)
    {
        var c = await _config.GetSiteConfigAsync(siteId);
        return c is null ? NotFound() : Ok(c);
    }

    [HttpPut("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateSite(Guid siteId, [FromBody] SiteConfiguration config) => Ok(await _config.UpdateSiteAsync(siteId, config, CurrentUser));

    [HttpPatch("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchSite(Guid siteId, [FromBody] Dictionary<string, object> updates) => Ok(await _config.PatchSiteAsync(siteId, updates, CurrentUser));

    [HttpDelete("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.Delete)]
    public async Task<IActionResult> DeleteSite(Guid siteId) { await _config.DeleteSiteConfigAsync(siteId); return NoContent(); }

    // ── AI ─────────────────────────────────────────────────────────────

    [HttpGet("ai/credentials")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAiCredentials(CancellationToken ct) => Ok(await _aiCredentialRepo.GetAllAsync(ct));

    [HttpPost("ai/credentials")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> CreateAiCredential([FromBody] AiProviderCredential credential, CancellationToken ct) => Ok(await _aiCredentialRepo.CreateAsync(credential, ct));

    [HttpDelete("ai/credentials/{credentialId:guid}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Delete)]
    public async Task<IActionResult> DeleteAiCredential(Guid credentialId, CancellationToken ct) { await _aiCredentialRepo.DeleteAsync(credentialId, ct); return NoContent(); }

    [HttpGet("ai/models")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAiModels(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default) => Ok(await _aiCatalog.ListModelsAsync(clientId, siteId, new AiModelSearchRequest { Search = search }, ct));
}

public record NatsConnectionTestRequest(string Url, string User, string Password);

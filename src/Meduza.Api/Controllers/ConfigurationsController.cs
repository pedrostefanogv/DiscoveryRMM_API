using Meduza.Core.Entities;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

/// <summary>
/// Gerencia configurações hierárquicas: Servidor → Cliente → Site.
/// Valores null em cliente/site indicam herança do nível superior.
/// </summary>
[ApiController]
[Route("api/configurations")]
public class ConfigurationsController : ControllerBase
{
    private readonly IConfigurationService _configService;
    private readonly IConfigurationResolver _resolver;

    public ConfigurationsController(IConfigurationService configService, IConfigurationResolver resolver)
    {
        _configService = configService;
        _resolver = resolver;
    }

    // ============ Server ============

    [HttpGet("server")]
    public async Task<IActionResult> GetServer()
    {
        var config = await _configService.GetServerConfigAsync();
        return Ok(config);
    }

    [HttpPut("server")]
    public async Task<IActionResult> UpdateServer([FromBody] ServerConfiguration config)
    {
        var (isValid, errors) = await _configService.ValidateAsync(config);
        if (!isValid) return BadRequest(new { errors });

        var updated = await _configService.UpdateServerAsync(config,
            HttpContext.Items["Username"] as string ?? "api");
        return Ok(updated);
    }

    [HttpPatch("server")]
    public async Task<IActionResult> PatchServer([FromBody] Dictionary<string, object> updates)
    {
        var updated = await _configService.PatchServerAsync(updates,
            HttpContext.Items["Username"] as string ?? "api");
        return Ok(updated);
    }

    [HttpPost("server/reset")]
    public async Task<IActionResult> ResetServer()
    {
        var reset = await _configService.ResetServerAsync(
            HttpContext.Items["Username"] as string ?? "api");
        return Ok(reset);
    }

    // ============ Client ============

    [HttpGet("clients/{clientId:guid}")]
    public async Task<IActionResult> GetClient(Guid clientId)
    {
        var config = await _configService.GetClientConfigAsync(clientId);
        return config is null ? NotFound() : Ok(config);
    }

    [HttpGet("clients/{clientId:guid}/effective")]
    public async Task<IActionResult> GetClientEffective(Guid clientId)
    {
        var client = await _configService.GetClientConfigAsync(clientId);
        var server = await _configService.GetServerConfigAsync();

        // Mesclagem manual: client define o que sobrescreve, server preenche os nulls
        var effective = new
        {
            RecoveryEnabled    = client?.RecoveryEnabled ?? server.RecoveryEnabled,
            DiscoveryEnabled   = client?.DiscoveryEnabled ?? server.DiscoveryEnabled,
            P2PFilesEnabled    = client?.P2PFilesEnabled ?? server.P2PFilesEnabled,
            SupportEnabled     = client?.SupportEnabled ?? server.SupportEnabled,
            AppStorePolicy     = client?.AppStorePolicy ?? server.AppStorePolicy,
            InventoryIntervalHours = client?.InventoryIntervalHours ?? server.InventoryIntervalHours,
            TokenExpirationDays    = client?.TokenExpirationDays ?? server.TokenExpirationDays,
            MaxTokensPerAgent      = client?.MaxTokensPerAgent ?? server.MaxTokensPerAgent,
            AgentHeartbeatIntervalSeconds = client?.AgentHeartbeatIntervalSeconds ?? server.AgentHeartbeatIntervalSeconds,
            AgentOfflineThresholdSeconds  = client?.AgentOfflineThresholdSeconds ?? server.AgentOfflineThresholdSeconds,
            InheritedFrom = client is null ? "Server" : "Client+Server"
        };
        return Ok(effective);
    }

    [HttpPut("clients/{clientId:guid}")]
    public async Task<IActionResult> UpsertClient(Guid clientId, [FromBody] ClientConfiguration config)
    {
        var (isValid, errors) = await _configService.ValidateAsync(config);
        if (!isValid) return BadRequest(new { errors });

        var updated = await _configService.UpdateClientAsync(clientId, config,
            HttpContext.Items["Username"] as string ?? "api");
        return Ok(updated);
    }

    [HttpPatch("clients/{clientId:guid}")]
    public async Task<IActionResult> PatchClient(Guid clientId, [FromBody] Dictionary<string, object> updates)
    {
        var updated = await _configService.PatchClientAsync(clientId, updates,
            HttpContext.Items["Username"] as string ?? "api");
        return Ok(updated);
    }

    [HttpDelete("clients/{clientId:guid}")]
    public async Task<IActionResult> DeleteClient(Guid clientId)
    {
        await _configService.DeleteClientConfigAsync(clientId,
            HttpContext.Items["Username"] as string ?? "api");
        return NoContent();
    }

    [HttpPost("clients/{clientId:guid}/reset/{propertyName}")]
    public async Task<IActionResult> ResetClientProperty(Guid clientId, string propertyName)
    {
        try
        {
            await _configService.ResetClientPropertyAsync(clientId, propertyName,
                HttpContext.Items["Username"] as string ?? "api");
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ============ Site ============

    [HttpGet("sites/{siteId:guid}")]
    public async Task<IActionResult> GetSite(Guid siteId)
    {
        var config = await _configService.GetSiteConfigAsync(siteId);
        return config is null ? NotFound() : Ok(config);
    }

    [HttpGet("sites/{siteId:guid}/effective")]
    public async Task<IActionResult> GetSiteEffective(Guid siteId)
    {
        try
        {
            var resolved = await _resolver.ResolveForSiteAsync(siteId);
            return Ok(resolved);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("sites/{siteId:guid}")]
    public async Task<IActionResult> UpsertSite(Guid siteId, [FromBody] SiteConfiguration config)
    {
        var (isValid, errors) = await _configService.ValidateAsync(config);
        if (!isValid) return BadRequest(new { errors });

        try
        {
            var updated = await _configService.UpdateSiteAsync(siteId, config,
                HttpContext.Items["Username"] as string ?? "api");
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPatch("sites/{siteId:guid}")]
    public async Task<IActionResult> PatchSite(Guid siteId, [FromBody] Dictionary<string, object> updates)
    {
        try
        {
            var updated = await _configService.PatchSiteAsync(siteId, updates,
                HttpContext.Items["Username"] as string ?? "api");
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpDelete("sites/{siteId:guid}")]
    public async Task<IActionResult> DeleteSite(Guid siteId)
    {
        await _configService.DeleteSiteConfigAsync(siteId,
            HttpContext.Items["Username"] as string ?? "api");
        return NoContent();
    }

    [HttpPost("sites/{siteId:guid}/reset/{propertyName}")]
    public async Task<IActionResult> ResetSiteProperty(Guid siteId, string propertyName)
    {
        try
        {
            await _configService.ResetSitePropertyAsync(siteId, propertyName,
                HttpContext.Items["Username"] as string ?? "api");
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

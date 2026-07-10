using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Configurations.Commands;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/configurations")]
public class ConfigurationsController(IMediator mediator) : ControllerBase
{
    private string? CurrentUser => HttpContext.Items["Username"] as string;

    // ── Server ──────────────────────────────────────────────────────────

    [HttpGet("server")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetServer()
    {
        var result = await mediator.Send(new GetServerConfigQuery());
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("server/metadata")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public IActionResult GetServerMetadata() => Ok(new { fields = new[] { "AgentOnlineGraceSeconds", "NatsEnabled", "NatsServerHostInternal" } });

    [HttpPut("server")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateServer([FromBody] ServerConfiguration config)
    {
        var result = await mediator.Send(new UpdateServerConfigCommand(config, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPatch("server")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchServer([FromBody] Dictionary<string, object> updates)
    {
        var result = await mediator.Send(new PatchServerConfigCommand(updates, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("server/reset")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> ResetServer()
    {
        var result = await mediator.Send(new ResetServerConfigCommand(CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("server/reporting")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetServerReporting()
    {
        var result = await mediator.Send(new GetServerReportingQuery());
        return result.Match<IActionResult>(success: value => Ok(value ?? new { }), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("server/reporting")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateServerReporting([FromBody] object reporting)
    {
        var result = await mediator.Send(new UpdateServerReportingCommand(reporting, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("server/object-storage/test")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Execute)]
    public async Task<IActionResult> TestObjectStorage(CancellationToken ct)
    {
        var result = await mediator.Send(new TestObjectStorageCommand(), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("server/nats/test")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Execute)]
    public async Task<IActionResult> TestNats([FromBody] NatsConnectionTestRequest req, CancellationToken ct)
    {
        var result = await mediator.Send(new TestNatsConnectionCommand(req.Url, req.User, req.Password), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPatch("server/nats")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchNats([FromBody] Dictionary<string, object> updates)
    {
        var result = await mediator.Send(new PatchNatsConfigCommand(updates, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Clients ────────────────────────────────────────────────────────

    [HttpGet("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.View)]
    public async Task<IActionResult> GetClient(Guid clientId)
    {
        var result = await mediator.Send(new GetClientConfigQuery(clientId));
        return result.Match<IActionResult>(success: c => c is null ? NotFound() : Ok(c), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateClient(Guid clientId, [FromBody] ClientConfiguration config)
    {
        var result = await mediator.Send(new UpdateClientConfigCommand(clientId, config, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPatch("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchClient(Guid clientId, [FromBody] Dictionary<string, object> updates)
    {
        var result = await mediator.Send(new PatchClientConfigCommand(clientId, updates, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("clients/{clientId:guid}")]
    [RequirePermission(ResourceType.ClientConfig, ActionType.Delete)]
    public async Task<IActionResult> DeleteClient(Guid clientId)
    {
        await mediator.Send(new DeleteClientConfigCommand(clientId));
        return NoContent();
    }

    // ── Sites ──────────────────────────────────────────────────────────

    [HttpGet("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.View)]
    public async Task<IActionResult> GetSite(Guid siteId)
    {
        var result = await mediator.Send(new GetSiteConfigQuery(siteId));
        return result.Match<IActionResult>(success: c => c is null ? NotFound() : Ok(c), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPut("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.Edit)]
    public async Task<IActionResult> UpdateSite(Guid siteId, [FromBody] SiteConfiguration config)
    {
        var result = await mediator.Send(new UpdateSiteConfigCommand(siteId, config, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPatch("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.Edit)]
    public async Task<IActionResult> PatchSite(Guid siteId, [FromBody] Dictionary<string, object> updates)
    {
        var result = await mediator.Send(new PatchSiteConfigCommand(siteId, updates, CurrentUser));
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("sites/{siteId:guid}")]
    [RequirePermission(ResourceType.SiteConfig, ActionType.Delete)]
    public async Task<IActionResult> DeleteSite(Guid siteId)
    {
        await mediator.Send(new DeleteSiteConfigCommand(siteId));
        return NoContent();
    }

    // ── AI ─────────────────────────────────────────────────────────────

    [HttpGet("ai/credentials")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAiCredentials(CancellationToken ct)
    {
        var result = await mediator.Send(new GetAiCredentialsQuery(), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("ai/credentials")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> CreateAiCredential([FromBody] AiProviderCredential credential, CancellationToken ct)
    {
        var result = await mediator.Send(new CreateAiCredentialCommand(credential), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("ai/credentials/{credentialId:guid}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Delete)]
    public async Task<IActionResult> DeleteAiCredential(Guid credentialId, CancellationToken ct)
    {
        await mediator.Send(new DeleteAiCredentialCommand(credentialId), ct);
        return NoContent();
    }

    [HttpGet("ai/models")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAiModels([FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetAiModelsQuery(clientId, siteId, search), ct);
        return result.Match<IActionResult>(success: Ok, failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}

public record NatsConnectionTestRequest(string Url, string User, string Password);

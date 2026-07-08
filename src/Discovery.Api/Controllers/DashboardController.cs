using Discovery.Core.Cqrs.Dashboard.Queries;
using Discovery.Core.Enums.Identity;
using Discovery.Api.Filters;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}")]
public class DashboardController : ControllerBase
{
    private readonly IMediator _mediator;

    public DashboardController(
        IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("dashboard/global/summary")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    public async Task<IActionResult> GetGlobalSummary([FromQuery] string? window = null, CancellationToken cancellationToken = default)
    {
        if (!TryParseWindow(window, out var parsedWindow, out var error))
            return BadRequest(new { error });

        var query = new GetGlobalSummaryQuery(parsedWindow);
        var result = await _mediator.Send(query, cancellationToken);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpGet("clients/{clientId:guid}/dashboard/summary")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetClientSummary(Guid clientId, [FromQuery] string? window = null, CancellationToken cancellationToken = default)
    {
        if (!TryParseWindow(window, out var parsedWindow, out var error))
            return BadRequest(new { error });

        var query = new GetClientSummaryQuery(clientId, parsedWindow);
        var result = await _mediator.Send(query, cancellationToken);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    [HttpGet("clients/{clientId:guid}/sites/{siteId:guid}/dashboard/summary")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetSiteSummary(
        Guid clientId,
        Guid siteId,
        [FromQuery] string? window = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseWindow(window, out var parsedWindow, out var error))
            return BadRequest(new { error });

        var query = new GetSiteSummaryQuery(clientId, siteId, parsedWindow);
        var result = await _mediator.Send(query, cancellationToken);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
        );
    }

    private static bool TryParseWindow(string? window, out TimeSpan parsedWindow, out string? error)
    {
        var normalized = window?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            parsedWindow = TimeSpan.FromHours(24);
            error = null;
            return true;
        }

        if (normalized.Equals("24h", StringComparison.OrdinalIgnoreCase))
        {
            parsedWindow = TimeSpan.FromHours(24);
            error = null;
            return true;
        }

        if (normalized.Equals("7d", StringComparison.OrdinalIgnoreCase))
        {
            parsedWindow = TimeSpan.FromDays(7);
            error = null;
            return true;
        }

        if (normalized.Equals("30d", StringComparison.OrdinalIgnoreCase))
        {
            parsedWindow = TimeSpan.FromDays(30);
            error = null;
            return true;
        }

        parsedWindow = TimeSpan.Zero;
        error = "Invalid window. Allowed values: 24h, 7d, 30d.";
        return false;
    }
}

using Discovery.Core.Interfaces;
using Discovery.Core.Enums.Identity;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}")]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboardService;
    private readonly IClientRepository _clientRepository;
    private readonly ISiteRepository _siteRepository;

    public DashboardController(
        IDashboardService dashboardService,
        IClientRepository clientRepository,
        ISiteRepository siteRepository)
    {
        _dashboardService = dashboardService;
        _clientRepository = clientRepository;
        _siteRepository = siteRepository;
    }

    [HttpGet("dashboard/global/summary")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    public async Task<IActionResult> GetGlobalSummary([FromQuery] string? window = null, CancellationToken cancellationToken = default)
    {
        if (!TryParseWindow(window, out var parsedWindow, out var error))
            return BadRequest(new { error });

        var summary = await _dashboardService.GetGlobalSummaryAsync(parsedWindow, cancellationToken);
        return Ok(summary);
    }

    [HttpGet("clients/{clientId:guid}/dashboard/summary")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View, ScopeSource.FromRoute)]
    public async Task<IActionResult> GetClientSummary(Guid clientId, [FromQuery] string? window = null, CancellationToken cancellationToken = default)
    {
        if (!TryParseWindow(window, out var parsedWindow, out var error))
            return BadRequest(new { error });

        var client = await _clientRepository.GetByIdAsync(clientId);
        if (client is null)
            return NotFound(new { error = "Client not found." });

        var summary = await _dashboardService.GetClientSummaryAsync(clientId, parsedWindow, cancellationToken);
        return Ok(summary);
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

        var site = await _siteRepository.GetByIdAsync(siteId);
        if (site is null || site.ClientId != clientId)
            return NotFound(new { error = "Site not found for this client." });

        var summary = await _dashboardService.GetSiteSummaryAsync(clientId, siteId, parsedWindow, cancellationToken);
        return Ok(summary);
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

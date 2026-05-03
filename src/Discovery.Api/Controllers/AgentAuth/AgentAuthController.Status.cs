using Discovery.Api.Hubs;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent-specific realtime status and health-check endpoints.
/// Agents should use these endpoints instead of /api/v1/realtime/status,
/// which requires user-level JWT authentication.
/// </summary>
public partial class AgentAuthController
{
    /// <summary>
    /// Returns realtime connection status relevant to the authenticated agent.
    /// This is the agent-safe alternative to GET /api/v1/realtime/status
    /// (which requires user JWT auth and rejects mdz_ tokens).
    /// Agents authenticate via the standard mdz_ token flow.
    /// </summary>
    [HttpGet("me/realtime/status")]
    public IActionResult GetAgentRealtimeStatus()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var messaging = HttpContext.RequestServices.GetService<IAgentMessaging>();
        var natsConnected = messaging?.IsConnected == true;
        var redisConnected = _redisService.IsConnected;

        return Ok(new
        {
            agentId,
            serverTimeUtc = DateTime.UtcNow,
            natsConnected,
            signalrConnectedAgents = AgentHub.ConnectedAgentCount,
            redisConnected,
            healthy = natsConnected && redisConnected,
            checkedAtUtc = DateTime.UtcNow
        });
    }
}

using Discovery.Core.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent AI Chat endpoints: sync, async, streaming.
/// </summary>
public partial class AgentAuthController
{
    [HttpPost("me/ai-chat")]
    public async Task<IActionResult> ChatSync([FromBody] AgentChatRequest request, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        try
        {
            var response = await _aiChatService.ProcessSyncAsync(agentId, request.Message, request.SessionId, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("me/ai-chat/async")]
    public async Task<IActionResult> ChatAsync([FromBody] AgentChatRequest request, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        try
        {
            var jobId = await _aiChatService.ProcessAsyncAsync(agentId, request.Message, request.SessionId, ct);
            return Accepted(new { jobId, status = "queued" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("me/ai-chat/jobs/{jobId}")]
    public async Task<IActionResult> GetAiChatJob(Guid jobId, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var job = await _aiChatService.GetJobStatusAsync(jobId, agentId, ct);
        if (job is null) return NotFound(new { error = "Job not found." });
        return Ok(job);
    }
}

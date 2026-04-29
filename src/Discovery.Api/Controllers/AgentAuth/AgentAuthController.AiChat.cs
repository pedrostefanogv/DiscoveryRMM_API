using Discovery.Core.DTOs;
using Discovery.Core.DTOs.AiChatDtos;
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

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        try
        {
            var response = await _aiChatService.SendMessageAsync(agentId, agent!.SiteId, request.Message, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("me/ai-chat/async")]
    public async Task<IActionResult> ChatAsync([FromBody] AgentChatRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        try
        {
            var job = await _aiChatService.EnqueueMessageAsync(agentId, agent!.SiteId, request.Message);
            return Accepted(new { jobId = job.JobId, status = "queued" });
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

        var job = await _aiChatService.GetJobAsync(jobId, ct);
        if (job is null) return NotFound(new { error = "Job not found." });
        return Ok(job);
    }

    [HttpPost("me/ai-chat/stream")]
    public async Task<IActionResult> ChatStream([FromBody] AgentChatRequest request, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        try
        {
            var result = await _aiChatService.CreateSessionAsync(agentId, agent!.SiteId, request.Message, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

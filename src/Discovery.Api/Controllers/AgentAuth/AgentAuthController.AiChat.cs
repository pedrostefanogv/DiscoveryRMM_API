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
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var response = await _aiChatService.ProcessSyncAsync(
                agentId, request.Message, request.SessionId, clientIp, request.MaxTokens, request.DepartmentId, ct);
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
            var jobId = await _aiChatService.ProcessAsyncAsync(
                agentId, request.Message, request.SessionId, request.MaxTokens, request.DepartmentId, ct);
            return Accepted(new { jobId, status = "queued" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("me/ai-chat/stream")]
    public async Task ChatStream([FromBody] AgentChatRequest request, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "Agent not authenticated." }, ct);
            return;
        }

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null)
        {
            HttpContext.Response.StatusCode = ((ObjectResult)blocked).StatusCode ?? 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ((ObjectResult)blocked).Value ?? new { error = "Agent blocked." }, ct);
            return;
        }

        HttpContext.Response.ContentType = "text/event-stream";
        HttpContext.Response.Headers.Append("Cache-Control", "no-cache");
        HttpContext.Response.Headers.Append("Connection", "keep-alive");
        HttpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            await foreach (var chunk in _aiChatService.StreamAsync(
                agentId, request.Message, request.SessionId, request.DepartmentId, ct))
            {
                if (chunk.Type == "error")
                {
                    await HttpContext.Response.WriteAsync(
                        $"data: {{\"type\":\"error\",\"error\":\"{EscapeSse(chunk.Error ?? "erro desconhecido")}\"}}\n\n", ct);
                    await HttpContext.Response.Body.FlushAsync(ct);
                    return;
                }

                var json = System.Text.Json.JsonSerializer.Serialize(chunk);
                await HttpContext.Response.WriteAsync($"data: {json}\n\n", ct);
                await HttpContext.Response.Body.FlushAsync(ct);

                if (chunk.Type == "done")
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Cliente desconectou — normal
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                await HttpContext.Response.WriteAsync(
                    $"data: {{\"type\":\"error\",\"error\":\"{EscapeSse(ex.Message)}\"}}\n\n", ct);
                await HttpContext.Response.Body.FlushAsync(ct);
            }
        }
    }

    private static string EscapeSse(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

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

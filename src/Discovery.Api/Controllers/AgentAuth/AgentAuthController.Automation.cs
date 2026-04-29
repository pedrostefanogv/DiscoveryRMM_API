using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent automation & command endpoints: policy sync, execution ack/result, commands.
/// </summary>
public partial class AgentAuthController
{
    [HttpPost("me/automation/policy-sync")]
    public async Task<IActionResult> SyncAutomationPolicy(
        [FromBody] AgentAutomationPolicySyncRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var correlationId = Request.Headers.TryGetValue("X-Correlation-Id", out var values)
            && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : Guid.NewGuid().ToString("N");

        var response = await _automationTaskService.SyncPolicyForAgentAsync(
            agentId,
            request ?? new AgentAutomationPolicySyncRequest(),
            HttpContext.Items["Username"] as string ?? "agent",
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            correlationId,
            cancellationToken);

        Response.Headers["X-Correlation-Id"] = correlationId;
        return Ok(response);
    }

    [HttpGet("me/commands")]
    public async Task<IActionResult> GetCommands([FromQuery] int limit = 50)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        return Ok(await _commandRepo.GetByAgentIdAsync(agentId, limit));
    }

    [HttpPost("me/automation/executions/{commandId:guid}/ack")]
    public async Task<IActionResult> AckAutomationExecution(Guid commandId, [FromBody] AutomationExecutionAckRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (_, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var command = await _commandRepo.GetByIdAsync(commandId);
        if (command is null || command.AgentId != agentId)
            return NotFound(new { error = "Command not found for this agent." });

        var correlationId = Request.Headers.TryGetValue("X-Correlation-Id", out var values)
            && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : null;

        var existing = await _automationExecutionReportRepository.GetByCommandIdAsync(commandId);
        if (existing is null)
        {
            await _automationExecutionReportRepository.CreateAsync(new AutomationExecutionReport
            {
                CommandId = commandId, AgentId = agentId,
                TaskId = request.TaskId, ScriptId = request.ScriptId,
                SourceType = request.SourceType, Status = AutomationExecutionStatus.Acknowledged,
                CorrelationId = correlationId, AckMetadataJson = request.MetadataJson, AcknowledgedAt = DateTime.UtcNow
            });
        }
        else
        {
            await _automationExecutionReportRepository.UpdateAckAsync(
                commandId, request.TaskId, request.ScriptId, request.MetadataJson, DateTime.UtcNow, correlationId);
        }

        if (command.Status == CommandStatus.Pending)
            await _commandRepo.UpdateStatusAsync(commandId, CommandStatus.Sent, command.Result, command.ExitCode, command.ErrorMessage);

        return Ok(new { acknowledged = true, commandId });
    }

    [HttpPost("me/automation/executions/{commandId:guid}/result")]
    public async Task<IActionResult> CompleteAutomationExecution(Guid commandId, [FromBody] AutomationExecutionResultRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var (agent, blocked) = await GetAgentOrBlockPendingAsync(agentId, allowPending: false);
        if (blocked is not null) return blocked;

        var command = await _commandRepo.GetByIdAsync(commandId);
        if (command is null || command.AgentId != agentId)
            return NotFound(new { error = "Command not found for this agent." });

        var correlationId = Request.Headers.TryGetValue("X-Correlation-Id", out var values)
            && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : null;

        var existing = await _automationExecutionReportRepository.GetByCommandIdAsync(commandId);
        if (existing is null)
        {
            await _automationExecutionReportRepository.CreateAsync(new AutomationExecutionReport
            {
                CommandId = commandId, AgentId = agentId,
                TaskId = request.TaskId, ScriptId = request.ScriptId,
                SourceType = request.SourceType,
                Status = request.Success ? AutomationExecutionStatus.Completed : AutomationExecutionStatus.Failed,
                CorrelationId = correlationId, ResultMetadataJson = request.MetadataJson,
                ResultReceivedAt = DateTime.UtcNow, ExitCode = request.ExitCode, ErrorMessage = request.ErrorMessage
            });
        }
        else
        {
            await _automationExecutionReportRepository.UpdateResultAsync(
                commandId, request.TaskId, request.ScriptId, request.Success,
                request.ExitCode, request.ErrorMessage, request.MetadataJson, DateTime.UtcNow, correlationId);
        }

        await _commandRepo.UpdateStatusAsync(commandId,
            request.Success ? CommandStatus.Completed : CommandStatus.Failed,
            request.MetadataJson, request.ExitCode, request.ErrorMessage);

        try
        {
            if (agent is not null)
            {
                var site = await _siteRepo.GetByIdAsync(agent.SiteId);
                if (site is not null)
                {
                    var labels = await _agentLabelRepository.GetByAgentIdAsync(agentId);
                    var report = await _automationExecutionReportRepository.GetByCommandIdAsync(commandId);
                    var monitoringEvent = TryBuildMonitoringEventFromAutomationResult(
                        site.ClientId, agent.SiteId, agentId, report?.Id, correlationId, request,
                        labels.Select(label => label.Label));

                    if (monitoringEvent is not null)
                    {
                        var createdEvent = await _monitoringEventRepository.CreateAsync(monitoringEvent);
                        await _autoTicketOrchestratorService.EvaluateAsync(createdEvent, HttpContext.RequestAborted);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ingest monitoring event for automation result {CommandId} from agent {AgentId}.", commandId, agentId);
        }

        return Ok(new { completed = true, commandId, success = request.Success });
    }
}

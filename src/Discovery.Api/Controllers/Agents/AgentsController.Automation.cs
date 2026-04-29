using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Agent automation endpoints: run-now, force-sync, and execution history.
/// </summary>
public partial class AgentsController
{
    private enum PackageCommandOperation { Install, Update, Remove, UpdateOrInstall }

    [HttpPost("{id:guid}/automation/tasks/{taskId:guid}/run-now")]
    public async Task<IActionResult> RunAutomationTaskNow(Guid id, Guid taskId, CancellationToken ct = default)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();
        var task = await _automationTaskService.GetByIdAsync(taskId, includeInactive: false, ct);
        if (task is null) return NotFound(new { error = "Automation task not found or inactive." });

        var command = await BuildAgentCommandFromTaskAsync(id, task, ct);
        var created = await _commandDispatcher.DispatchAsync(command, ct);
        await CreateExecutionReportAsync(created, task.Id, task.ScriptId, AutomationExecutionSourceType.RunNow, new { mode = "task-run-now", actionType = task.ActionType.ToString() });
        return CreatedAtAction(nameof(GetCommands), new { id }, new { command = created, automationTaskId = task.Id, actionType = task.ActionType.ToString() });
    }

    [HttpPost("{id:guid}/automation/scripts/{scriptId:guid}/run-now")]
    public async Task<IActionResult> RunAutomationScriptNow(Guid id, Guid scriptId, CancellationToken ct = default)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();
        var script = await _automationScriptService.GetByIdAsync(scriptId, includeInactive: false, ct);
        if (script is null) return NotFound(new { error = "Automation script not found or inactive." });

        var command = new AgentCommand { AgentId = id, CommandType = CommandType.Script, Payload = script.Content };
        var created = await _commandDispatcher.DispatchAsync(command, ct);
        await CreateExecutionReportAsync(created, null, script.Id, AutomationExecutionSourceType.RunNow, new { mode = "script-run-now", version = script.Version, contentHash = script.ContentHashSha256 });
        return CreatedAtAction(nameof(GetCommands), new { id }, new { command = created, automationScriptId = script.Id, scriptVersion = script.Version, contentHash = script.ContentHashSha256 });
    }

    [HttpPost("{id:guid}/automation/force-sync")]
    public async Task<IActionResult> ForceAutomationSync(Guid id, [FromBody] ForceAutomationSyncRequest? request)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();
        var normalized = request ?? new ForceAutomationSyncRequest();
        var payload = JsonSerializer.Serialize(new { Operation = "force-sync", Policies = normalized.Policies, Inventory = normalized.Inventory, Software = normalized.Software, AppStore = normalized.AppStore, RequestedAt = DateTime.UtcNow });
        var command = new AgentCommand { AgentId = id, CommandType = CommandType.SystemInfo, Payload = payload };
        var created = await _commandDispatcher.DispatchAsync(command);
        await CreateExecutionReportAsync(created, null, null, AutomationExecutionSourceType.ForceSync, normalized);
        return CreatedAtAction(nameof(GetCommands), new { id }, new { command = created, sync = normalized });
    }

    [HttpGet("{id:guid}/automation/executions")]
    public async Task<IActionResult> GetAutomationExecutionHistory(Guid id, [FromQuery] int limit = 50)
    {
        var agent = await _agentRepo.GetByIdAsync(id);
        if (agent is null) return NotFound();
        var items = await _automationExecutionReportRepository.GetByAgentIdAsync(id, limit);
        return Ok(items.Select(item => new AutomationExecutionReportDto
        {
            Id = item.Id, CommandId = item.CommandId, AgentId = item.AgentId, TaskId = item.TaskId, ScriptId = item.ScriptId,
            SourceType = item.SourceType.ToString(), Status = item.Status.ToString(), CorrelationId = item.CorrelationId,
            CreatedAt = item.CreatedAt, AcknowledgedAt = item.AcknowledgedAt, ResultReceivedAt = item.ResultReceivedAt,
            ExitCode = item.ExitCode, ErrorMessage = item.ErrorMessage,
            RequestMetadataJson = item.RequestMetadataJson, AckMetadataJson = item.AckMetadataJson, ResultMetadataJson = item.ResultMetadataJson
        }));
    }

    private async Task CreateExecutionReportAsync(AgentCommand command, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, object requestMetadata)
    {
        await _automationExecutionReportRepository.CreateAsync(new AutomationExecutionReport
        {
            CommandId = command.Id, AgentId = command.AgentId, TaskId = taskId, ScriptId = scriptId,
            SourceType = sourceType, Status = AutomationExecutionStatus.Dispatched, RequestMetadataJson = JsonSerializer.Serialize(requestMetadata)
        });
    }

    private async Task<AgentCommand> BuildAgentCommandFromTaskAsync(Guid agentId, AutomationTaskDetailDto task, CancellationToken ct)
    {
        _ = ct;
        return task.ActionType switch
        {
            AutomationTaskActionType.RunScript => await BuildRunScriptCommandAsync(agentId, task),
            AutomationTaskActionType.InstallPackage => BuildPackageCommand(agentId, task, PackageCommandOperation.Install),
            AutomationTaskActionType.UpdatePackage => BuildPackageCommand(agentId, task, PackageCommandOperation.Update),
            AutomationTaskActionType.RemovePackage => BuildPackageCommand(agentId, task, PackageCommandOperation.Remove),
            AutomationTaskActionType.UpdateOrInstallPackage => BuildPackageCommand(agentId, task, PackageCommandOperation.UpdateOrInstall),
            AutomationTaskActionType.CustomCommand => BuildCustomCommand(agentId, task),
            _ => throw new InvalidOperationException("Unsupported automation task action type.")
        };
    }

    private async Task<AgentCommand> BuildRunScriptCommandAsync(Guid agentId, AutomationTaskDetailDto task)
    {
        if (!task.ScriptId.HasValue) throw new InvalidOperationException("Automation task has no ScriptId.");
        var script = await _automationScriptService.GetByIdAsync(task.ScriptId.Value, includeInactive: false);
        if (script is null) throw new InvalidOperationException("Referenced automation script not found or inactive.");
        return new AgentCommand { AgentId = agentId, CommandType = CommandType.Script, Payload = script.Content };
    }

    private static AgentCommand BuildPackageCommand(Guid agentId, AutomationTaskDetailDto task, PackageCommandOperation operation)
    {
        if (!task.InstallationType.HasValue || string.IsNullOrWhiteSpace(task.PackageId))
            throw new InvalidOperationException("Package action requires InstallationType and PackageId.");

        var packageId = task.PackageId.Trim();
        var payload = task.InstallationType.Value switch
        {
            AppInstallationType.Winget => operation switch
            {
                PackageCommandOperation.Install => $"winget install --id {packageId} --silent --accept-package-agreements --accept-source-agreements",
                PackageCommandOperation.Update => $"winget upgrade --id {packageId} --silent --accept-package-agreements --accept-source-agreements",
                PackageCommandOperation.Remove => $"winget uninstall --id {packageId} --silent --accept-source-agreements",
                _ => $"winget upgrade --id {packageId} --silent --accept-package-agreements --accept-source-agreements ; if ($LASTEXITCODE -ne 0) {{ winget install --id {packageId} --silent --accept-package-agreements --accept-source-agreements }}"
            },
            AppInstallationType.Chocolatey => operation switch
            {
                PackageCommandOperation.Install => $"choco install {packageId} -y",
                PackageCommandOperation.Update => $"choco upgrade {packageId} -y",
                PackageCommandOperation.Remove => $"choco uninstall {packageId} -y",
                _ => $"choco upgrade {packageId} -y --ignore-not-installed ; if ($LASTEXITCODE -ne 0) {{ choco install {packageId} -y }}"
            },
            _ => throw new InvalidOperationException("Unsupported package installation type.")
        };
        return new AgentCommand { AgentId = agentId, CommandType = CommandType.PowerShell, Payload = payload };
    }

    private static AgentCommand BuildCustomCommand(Guid agentId, AutomationTaskDetailDto task)
    {
        if (string.IsNullOrWhiteSpace(task.CommandPayload)) throw new InvalidOperationException("Custom action requires CommandPayload.");
        return new AgentCommand { AgentId = agentId, CommandType = CommandType.PowerShell, Payload = task.CommandPayload };
    }
}

using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Cqrs.Agents.Automation.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class RunAutomationTaskCommandHandler(
    IAgentRepository agentRepo,
    IAutomationTaskService taskService,
    IAutomationScriptService scriptService,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<RunAutomationTaskCommand, Result<AutomationExecutionDto>>
{
    public async Task<Result<AutomationExecutionDto>> Handle(RunAutomationTaskCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<AutomationExecutionDto>.Failure(Error.NotFound("Agent not found."));

        var task = await taskService.GetByIdAsync(cmd.TaskId, includeInactive: false, ct);
        if (task is null) return Result<AutomationExecutionDto>.Failure(Error.NotFound("Automation task not found or inactive."));

        var command = await BuildAgentCommandFromTaskAsync(cmd.AgentId, task, scriptService, ct);
        var created = await dispatcher.DispatchAsync(command, ct);
        await CreateReportAsync(reportRepo, created, task.Id, task.ScriptId, AutomationExecutionSourceType.RunNow,
            new { mode = "task-run-now", actionType = task.ActionType.ToString() });

        return Result<AutomationExecutionDto>.Success(new AutomationExecutionDto(created.Id, created.Status.ToString(), created.CreatedAt));
    }

    internal static async Task<AgentCommand> BuildAgentCommandFromTaskAsync(Guid agentId, AutomationTaskDetailDto task, IAutomationScriptService scriptService, CancellationToken ct)
    {
        _ = ct;
        return task.ActionType switch
        {
            AutomationTaskActionType.RunScript => await BuildRunScriptCommandAsync(agentId, task, scriptService),
            AutomationTaskActionType.InstallPackage => BuildPackageCommand(agentId, task, "install"),
            AutomationTaskActionType.UpdatePackage => BuildPackageCommand(agentId, task, "update"),
            AutomationTaskActionType.RemovePackage => BuildPackageCommand(agentId, task, "remove"),
            AutomationTaskActionType.UpdateOrInstallPackage => BuildPackageCommand(agentId, task, "update-or-install"),
            AutomationTaskActionType.CustomCommand => BuildCustomCommand(agentId, task),
            _ => throw new InvalidOperationException("Unsupported automation task action type.")
        };
    }

    private static async Task<AgentCommand> BuildRunScriptCommandAsync(Guid agentId, AutomationTaskDetailDto task, IAutomationScriptService scriptService)
    {
        if (!task.ScriptId.HasValue) throw new InvalidOperationException("Automation task has no ScriptId.");
        var script = await scriptService.GetByIdAsync(task.ScriptId.Value, includeInactive: false);
        if (script is null) throw new InvalidOperationException("Referenced automation script not found or inactive.");
        return new AgentCommand { AgentId = agentId, CommandType = CommandType.Script, Payload = script.Content };
    }

    private static AgentCommand BuildPackageCommand(Guid agentId, AutomationTaskDetailDto task, string operation)
    {
        if (!task.InstallationType.HasValue || string.IsNullOrWhiteSpace(task.PackageId))
            throw new InvalidOperationException("Package action requires InstallationType and PackageId.");

        var packageId = task.PackageId.Trim();
        var payload = task.InstallationType.Value switch
        {
            AppInstallationType.Winget => operation switch
            {
                "install" => $"winget install --id {packageId} --silent --accept-package-agreements --accept-source-agreements",
                "update" => $"winget upgrade --id {packageId} --silent --accept-package-agreements --accept-source-agreements",
                "remove" => $"winget uninstall --id {packageId} --silent --accept-source-agreements",
                _ => $"winget upgrade --id {packageId} --silent --accept-package-agreements --accept-source-agreements ; if ($LASTEXITCODE -ne 0) {{ winget install --id {packageId} --silent --accept-package-agreements --accept-source-agreements }}"
            },
            AppInstallationType.Chocolatey => operation switch
            {
                "install" => $"choco install {packageId} -y",
                "update" => $"choco upgrade {packageId} -y",
                "remove" => $"choco uninstall {packageId} -y",
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

    internal static async Task CreateReportAsync(IAutomationExecutionReportRepository reportRepo, AgentCommand command, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, object metadata)
    {
        await reportRepo.CreateAsync(new AutomationExecutionReport
        {
            CommandId = command.Id, AgentId = command.AgentId, TaskId = taskId, ScriptId = scriptId,
            SourceType = sourceType, Status = AutomationExecutionStatus.Dispatched, RequestMetadataJson = JsonSerializer.Serialize(metadata)
        });
    }
}

public sealed class RunAutomationScriptCommandHandler(
    IAgentRepository agentRepo,
    IAutomationScriptService scriptService,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<RunAutomationScriptCommand, Result<AutomationExecutionDto>>
{
    public async Task<Result<AutomationExecutionDto>> Handle(RunAutomationScriptCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<AutomationExecutionDto>.Failure(Error.NotFound("Agent not found."));

        var script = await scriptService.GetByIdAsync(cmd.ScriptId, includeInactive: false, ct);
        if (script is null) return Result<AutomationExecutionDto>.Failure(Error.NotFound("Automation script not found or inactive."));

        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.Script, Payload = script.Content };
        var created = await dispatcher.DispatchAsync(command, ct);
        await RunAutomationTaskCommandHandler.CreateReportAsync(reportRepo, created, null, script.Id, AutomationExecutionSourceType.RunNow,
            new { mode = "script-run-now", version = script.Version, contentHash = script.ContentHashSha256 });

        return Result<AutomationExecutionDto>.Success(new AutomationExecutionDto(created.Id, created.Status.ToString(), created.CreatedAt));
    }
}

public sealed class ForceAutomationSyncCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<ForceAutomationSyncCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ForceAutomationSyncCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var payload = JsonSerializer.Serialize(new { Operation = "force-sync", TaskIds = cmd.TaskIds, RequestedAt = DateTime.UtcNow });
        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.SystemInfo, Payload = payload };
        var created = await dispatcher.DispatchAsync(command, ct);
        await RunAutomationTaskCommandHandler.CreateReportAsync(reportRepo, created, null, null, AutomationExecutionSourceType.ForceSync, new { taskIds = cmd.TaskIds });

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class RefreshAgentDataCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher
) : IRequestHandler<RefreshAgentDataCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RefreshAgentDataCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var payload = JsonSerializer.Serialize(new
        {
            Operation = "refresh-on-demand",
            Categories = cmd.Categories ?? [],
            RequestedAt = DateTime.UtcNow
        });

        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.SystemInfo, Payload = payload };
        await dispatcher.DispatchAsync(command, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
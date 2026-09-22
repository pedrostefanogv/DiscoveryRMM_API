using Discovery.Core.Cqrs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using System.Text.Json;

namespace Discovery.Core.Cqrs.Agents.StartupTasks.Commands;

/// <summary>
/// Habilita/desabilita um item de inicialização do Windows no agent
/// (origem: registro Run/RunOnce, pasta Startup ou serviço automático).
/// </summary>
public sealed record StartupItemActionCommand(
    Guid AgentId,
    string Action,      // enable | disable
    string Type,        // registry | folder | service
    string Name,
    string? Source,
    string? Hive = null // HKLM | HKCU | HKU:<SID>
) : ICommand<Result<VoidResult>>;

/// <summary>Campos editáveis de uma tarefa agendada (gatilho e ação).</summary>
public sealed record ScheduledTaskEditDto(
    string TriggerType,           // daily | weekly | once | logon | boot
    string? Time,                 // HH:mm (daily/weekly) ou yyyy-MM-dd HH:mm (once)
    int[]? DaysOfWeek,            // 0=Dom..6=Sáb (weekly)
    int DaysInterval = 1,         // intervalo em dias (daily > 1)
    string? ActionPath = null,    // opcional: altera o que a tarefa executa
    string? ActionArgs = null
);

/// <summary>
/// Executa uma ação sobre uma tarefa agendada do agent (habilitar,
/// desabilitar, executar agora, excluir ou editar gatilho/ação).
/// </summary>
public sealed record ScheduledTaskActionCommand(
    Guid AgentId,
    string Action,     // enable | disable | run | delete | edit
    string TaskName,
    string? TaskPath,
    ScheduledTaskEditDto? Edit
) : ICommand<Result<VoidResult>>;

public sealed class StartupItemActionCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher
) : IRequestHandler<StartupItemActionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(StartupItemActionCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var payload = JsonSerializer.Serialize(new
        {
            action = cmd.Action.ToLowerInvariant(),
            type = cmd.Type.ToLowerInvariant(),
            name = cmd.Name,
            source = cmd.Source,
            hive = cmd.Hive,
            requestedAt = DateTime.UtcNow
        });

        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.StartupItem, Payload = payload };
        await dispatcher.DispatchAsync(command, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ScheduledTaskActionCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher
) : IRequestHandler<ScheduledTaskActionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ScheduledTaskActionCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        object? edit = null;
        if (cmd.Action.Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            if (cmd.Edit is null)
                return Result<VoidResult>.Failure(Error.Validation("edit", "campo 'edit' é obrigatório para ação 'edit'."));
            edit = new
            {
                triggerType = cmd.Edit.TriggerType.ToLowerInvariant(),
                time = cmd.Edit.Time,
                daysOfWeek = cmd.Edit.DaysOfWeek,
                daysInterval = cmd.Edit.DaysInterval,
                actionPath = cmd.Edit.ActionPath,
                actionArgs = cmd.Edit.ActionArgs
            };
        }

        var payload = JsonSerializer.Serialize(new
        {
            action = cmd.Action.ToLowerInvariant(),
            taskPath = cmd.TaskPath,
            taskName = cmd.TaskName,
            edit,
            requestedAt = DateTime.UtcNow
        });

        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.ScheduledTask, Payload = payload };
        await dispatcher.DispatchAsync(command, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

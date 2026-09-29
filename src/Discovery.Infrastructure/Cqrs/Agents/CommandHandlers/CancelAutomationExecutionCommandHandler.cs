using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

/// <summary>
/// Cancela uma execução ainda não finalizada.
///
/// Dois efeitos: marca o COMANDO como <c>Cancelled</c> (estado terminal, então a
/// reentrega automática para de reenviá-lo) e marca a EXECUÇÃO como
/// <c>Cancelled</c> no histórico. É a forma de abortar um lote/comando que ficou
/// enfileirado para agentes offline dentro da janela de reentrega.
///
/// Limitação: não interrompe um comando que o agente já começou a executar — o
/// protocolo atual não tem cancelamento em runtime no lado do agente.
/// </summary>
public sealed class CancelAutomationExecutionCommandHandler(
    IAutomationExecutionReportRepository reportRepo,
    ICommandRepository commandRepo
) : IRequestHandler<CancelAutomationExecutionCommand, Result<AutomationExecutionDto>>
{
    private const string CancelReason = "Cancelada pelo operador.";

    public async Task<Result<AutomationExecutionDto>> Handle(CancelAutomationExecutionCommand cmd, CancellationToken ct)
    {
        var report = await reportRepo.GetByIdAsync(cmd.ExecutionId);

        // Anti-IDOR: a execução precisa pertencer ao agent informado na rota.
        if (report is null || report.AgentId != cmd.AgentId)
            return Result<AutomationExecutionDto>.Failure(Error.NotFound("Automation execution not found for this agent."));

        if (report.Status is AutomationExecutionStatus.Completed
            or AutomationExecutionStatus.Failed
            or AutomationExecutionStatus.Cancelled)
        {
            return Result<AutomationExecutionDto>.Failure(
                Error.Conflict("Execução já finalizada — não é possível cancelar."));
        }

        if (report.CommandId.HasValue)
        {
            var command = await commandRepo.GetByIdAsync(report.CommandId.Value);

            // Comando já terminal (ex.: timeout da reentrega) não é tocado.
            if (command is not null
                && command.Status is not (CommandStatus.Completed
                    or CommandStatus.Failed
                    or CommandStatus.Cancelled
                    or CommandStatus.Timeout))
            {
                await commandRepo.UpdateStatusAsync(command.Id, CommandStatus.Cancelled, null, null, CancelReason);
            }
        }

        var cancelled = await reportRepo.MarkCancelledAsync(cmd.ExecutionId, CancelReason, DateTime.UtcNow);
        if (!cancelled)
        {
            // Corrida: o resultado do agent chegou entre a leitura e o UPDATE.
            return Result<AutomationExecutionDto>.Failure(
                Error.Conflict("Execução já finalizada — não é possível cancelar."));
        }

        return Result<AutomationExecutionDto>.Success(new AutomationExecutionDto(
            report.Id, AutomationExecutionStatus.Cancelled.ToString(), report.CreatedAt));
    }
}

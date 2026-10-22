using System.Diagnostics;
using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MediatR;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using CoreCqrs = Discovery.Core.Cqrs;

namespace Discovery.Infrastructure.Cqrs.Tickets;

/// <summary>
/// Executa o runbook de uma TicketMacro no chamado (M242).
/// Cada passo usa o caminho REAL de execução de scripts de automação
/// (dispatcher de comandos + AutomationExecutionReport, como no "run now"),
/// aguardando a conclusão. Falha de um passo NÃO aborta os seguintes.
/// O resultado de cada passo é anexado ao chamado como comentário (evidência).
/// </summary>
public sealed class ExecuteTicketRunbookCommandHandler(
    DiscoveryDbContext db,
    IAutomationScriptService scriptService,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo,
    IMediator mediator,
    ILogger<ExecuteTicketRunbookCommandHandler> logger
) : IRequestHandler<ExecuteTicketRunbookCommand, Result<ExecuteTicketRunbookResult>>
{
    private static readonly TimeSpan StepWaitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private const int OutputPreviewLimit = 400;

    public async Task<Result<ExecuteTicketRunbookResult>> Handle(ExecuteTicketRunbookCommand cmd, CancellationToken ct)
    {
        var macro = await db.TicketMacros.AsNoTracking().FirstOrDefaultAsync(m => m.Id == cmd.MacroId, ct);
        if (macro is null || !macro.IsActive)
            return Result<ExecuteTicketRunbookResult>.Failure(CoreCqrs.Error.NotFound("Macro não encontrada."));

        var steps = RunbookStepList.FromJson(macro.RunbookStepsJson);
        if (steps.Count == 0)
            return Result<ExecuteTicketRunbookResult>.Failure(
                CoreCqrs.Error.Validation("RunbookSteps", "Macro não possui runbook configurado."));

        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == cmd.TicketId, ct);
        if (ticket is null)
            return Result<ExecuteTicketRunbookResult>.Failure(CoreCqrs.Error.NotFound("Chamado não encontrado."));

        if (ticket.AgentId is null)
            return Result<ExecuteTicketRunbookResult>.Failure(
                CoreCqrs.Error.Validation("AgentId", "Chamado sem agent vinculado: o runbook precisa de uma máquina para executar."));

        var agentId = ticket.AgentId.Value;
        var stepResults = new List<RunbookStepResult>();

        var abortRemaining = false;

        foreach (var step in steps.OrderBy(s => s.Order))
        {
            if (abortRemaining)
            {
                stepResults.Add(new RunbookStepResult(
                    step.Order, step.AutomationScriptId, false, null,
                    "Passo pulado: passo anterior falhou com StopOnFailure.", 0, null));
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var (ok, exitCode, outputPreview) = await ExecuteStepAsync(step, agentId, cmd, ct);
            stopwatch.Stop();
            if (!ok && step.StopOnFailure)
                abortRemaining = true;

            var label = string.IsNullOrWhiteSpace(step.EvidenceLabel)
                ? macro.Name
                : step.EvidenceLabel.Trim();
            var commentId = await AttachEvidenceAsync(cmd, step, label, ok, exitCode, outputPreview, ct);

            stepResults.Add(new RunbookStepResult(
                step.Order, step.AutomationScriptId, ok, exitCode,
                Truncate(outputPreview), stopwatch.ElapsedMilliseconds, commentId));
        }

        return Result<ExecuteTicketRunbookResult>.Success(new ExecuteTicketRunbookResult(cmd.TicketId, stepResults));
    }

    private async Task<(bool Ok, int? ExitCode, string? OutputPreview)> ExecuteStepAsync(
        RunbookStep step, Guid agentId, ExecuteTicketRunbookCommand cmd, CancellationToken ct)
    {
        var script = await scriptService.GetByIdAsync(step.AutomationScriptId, includeInactive: false, ct);
        if (script is null)
            return (false, null, "Script de automação não encontrado ou inativo.");

        // Timeout por passo: default 120s; step.TimeoutSeconds sobrescreve (10–900).
        var effectiveTimeout = step.TimeoutSeconds is { } s ? Math.Clamp(s, 10, 900) : 120;
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stepCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout));
        ct = stepCts.Token;

        try
        {
            // Mesmo caminho do RunAutomationScriptCommandHandler (run now).
            var command = new AgentCommand { AgentId = agentId, CommandType = CommandType.Script, Payload = script.Content };
            var created = await dispatcher.DispatchAsync(command, ct);
            await RunAutomationTaskCommandHandler.CreateReportAsync(
                reportRepo, created, null, script.Id, AutomationExecutionSourceType.RunNow,
                new { mode = "ticket-runbook", ticketId = cmd.TicketId, macroId = cmd.MacroId, step.Order, step.ArgsJson },
                $"ticket-runbook:{cmd.TicketId}");

            var report = await WaitForCompletionAsync(created.Id, ct);
            if (report is null)
                return (false, null, "Timeout aguardando a conclusão da execução no agent.");

            var ok = report.Status == AutomationExecutionStatus.Completed;
            var preview = report.ResultMetadataJson ?? report.ErrorMessage;
            return (ok, report.ExitCode, preview);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Runbook: falha ao executar script {ScriptId} no passo {Order}", step.AutomationScriptId, step.Order);
            return (false, null, ex.Message);
        }
    }

    private async Task<AutomationExecutionReport?> WaitForCompletionAsync(Guid commandId, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(StepWaitTimeout);

        while (true)
        {
            var report = await reportRepo.GetByCommandIdAsync(commandId);
            if (report is { Status: AutomationExecutionStatus.Completed or AutomationExecutionStatus.Failed or AutomationExecutionStatus.Cancelled })
                return report;

            await Task.Delay(PollInterval, timeoutCts.Token);
        }
    }

    private async Task<Guid?> AttachEvidenceAsync(
        ExecuteTicketRunbookCommand cmd, RunbookStep step, string label,
        bool ok, int? exitCode, string? outputPreview, CancellationToken ct)
    {
        try
        {
            var preview = Truncate(outputPreview) ?? "(sem saída)";
            var content = $"[Runbook] {label} — passo {step.Order}: {(ok ? "OK" : "FALHA")} (exitCode {exitCode?.ToString() ?? "-"}): {preview}";
            var result = await mediator.Send(new AddTicketCommentCommand(cmd.TicketId, content, true, cmd.OperatorId, cmd.OperatorName), ct);
            return result.IsSuccess ? result.Value.CommentId : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Runbook: falha ao anexar evidência do passo {Order} ao chamado {TicketId}", step.Order, cmd.TicketId);
            return null;
        }
    }

    internal static string? Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        return text.Length <= OutputPreviewLimit ? text : text[..OutputPreviewLimit] + "…";
    }
}
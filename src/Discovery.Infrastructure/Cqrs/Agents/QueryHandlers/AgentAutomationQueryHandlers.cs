using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Cqrs.Agents.Automation.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class GetAutomationExecutionsQueryHandler(
    IAgentRepository agentRepo,
    IAutomationExecutionReportRepository reportRepo,
    IAutomationTaskRepository taskRepo,
    IAutomationScriptRepository scriptRepo
) : IRequestHandler<GetAutomationExecutionsQuery, Result<IReadOnlyList<AutomationExecutionDto>>>
{
    public async Task<Result<IReadOnlyList<AutomationExecutionDto>>> Handle(GetAutomationExecutionsQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<IReadOnlyList<AutomationExecutionDto>>.Failure(Error.NotFound("Agent not found."));

        var items = await reportRepo.GetByAgentIdAsync(
            q.AgentId, q.Limit, q.Status, q.SourceType, q.TaskId, q.ScriptId, q.CorrelationId);

        // Nomes para a coluna Task/Script da página de operações (que antes só
        // mostrava GUIDs). Duas consultas em lote — a página faz polling de 3s
        // enquanto há execução em andamento, então resolver id a id (N+1) pesava.
        var taskNames = await taskRepo.GetNamesByIdsAsync(
            items.Where(i => i.TaskId.HasValue).Select(i => i.TaskId!.Value).ToArray());
        var scriptNames = await scriptRepo.GetNamesByIdsAsync(
            items.Where(i => i.ScriptId.HasValue).Select(i => i.ScriptId!.Value).ToArray());

        var dtos = items.Select(e => new AutomationExecutionDto(
            e.Id,
            e.Status.ToString(),
            e.CreatedAt,
            e.CommandId,
            e.AgentId,
            e.TaskId,
            e.ScriptId,
            e.SourceType.ToString(),
            e.CorrelationId,
            e.AcknowledgedAt,
            e.ResultReceivedAt,
            e.ExitCode,
            e.ErrorMessage,
            e.RequestMetadataJson,
            e.AckMetadataJson,
            e.ResultMetadataJson,
            e.TaskId.HasValue && taskNames.TryGetValue(e.TaskId.Value, out var taskName) && !string.IsNullOrWhiteSpace(taskName) ? taskName : null,
            e.ScriptId.HasValue && scriptNames.TryGetValue(e.ScriptId.Value, out var scriptName) && !string.IsNullOrWhiteSpace(scriptName) ? scriptName : null)).ToList();

        return Result<IReadOnlyList<AutomationExecutionDto>>.Success(dtos);
    }
}

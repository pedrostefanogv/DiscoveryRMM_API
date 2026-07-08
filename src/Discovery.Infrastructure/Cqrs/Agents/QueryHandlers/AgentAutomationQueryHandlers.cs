using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Cqrs.Agents.Automation.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class GetAutomationExecutionsQueryHandler(
    IAgentRepository agentRepo,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<GetAutomationExecutionsQuery, Result<IReadOnlyList<AutomationExecutionDto>>>
{
    public async Task<Result<IReadOnlyList<AutomationExecutionDto>>> Handle(GetAutomationExecutionsQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<IReadOnlyList<AutomationExecutionDto>>.Failure(Error.NotFound("Agent not found."));

        var items = await reportRepo.GetByAgentIdAsync(q.AgentId, q.Limit);
        var dtos = items.Select(e => new AutomationExecutionDto(e.Id, e.Status.ToString(), e.CreatedAt)).ToList();
        return Result<IReadOnlyList<AutomationExecutionDto>>.Success(dtos);
    }
}
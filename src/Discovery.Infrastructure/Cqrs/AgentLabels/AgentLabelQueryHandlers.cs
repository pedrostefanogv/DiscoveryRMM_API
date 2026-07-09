using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Cqrs.AgentLabels.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentLabels;

public sealed class ListAgentLabelsQueryHandler(ILabelService svc)
    : IRequestHandler<ListAgentLabelsQuery, Result<IReadOnlyList<AgentLabelDto>>>
{
    public async Task<Result<IReadOnlyList<AgentLabelDto>>> Handle(ListAgentLabelsQuery q, CancellationToken ct)
    {
        if (!q.AgentId.HasValue)
            return Result<IReadOnlyList<AgentLabelDto>>.Failure(Error.Validation("agentId", "Agent ID is required."));

        var labels = await svc.GetByAgentIdAsync(q.AgentId.Value, ct);
        var dtos = labels.Select(l => new AgentLabelDto(l.Id, l.AgentId, l.Label, l.SourceType.ToString(), l.CreatedAt))
            .ToList().AsReadOnly();
        return Result<IReadOnlyList<AgentLabelDto>>.Success(dtos);
    }
}

public sealed class GetDistinctLabelsQueryHandler(ILabelService svc)
    : IRequestHandler<GetDistinctLabelsQuery, Result<IReadOnlyList<string>>>
{
    public async Task<Result<IReadOnlyList<string>>> Handle(GetDistinctLabelsQuery q, CancellationToken ct)
    {
        var labels = await svc.GetDistinctLabelsAsync(ct);
        return Result<IReadOnlyList<string>>.Success(labels);
    }
}

public sealed class RemoveAgentLabelCommandHandler(ILabelService svc)
    : IRequestHandler<RemoveAgentLabelCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveAgentLabelCommand cmd, CancellationToken ct)
    {
        await svc.DeleteAsync(cmd.LabelId, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

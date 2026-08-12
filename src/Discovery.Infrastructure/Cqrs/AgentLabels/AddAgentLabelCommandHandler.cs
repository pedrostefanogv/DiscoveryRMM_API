using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Cqrs.AgentLabels.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentLabels;

public sealed class AddAgentLabelCommandHandler(ILabelService svc) : IRequestHandler<AddAgentLabelCommand, Result<AgentLabelDto>>
{
    public async Task<Result<AgentLabelDto>> Handle(AddAgentLabelCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Label))
            return Result<AgentLabelDto>.Failure(Error.Validation("label", "Label is required."));

        var existing = await svc.GetByAgentIdAsync(cmd.AgentId, ct);
        if (existing.Any(l => string.Equals(l.Label, cmd.Label, StringComparison.OrdinalIgnoreCase)))
            return Result<AgentLabelDto>.Failure(Error.Conflict($"Agent already has label '{cmd.Label}'."));

        var label = await svc.AddAsync(new AgentLabel { AgentId = cmd.AgentId, Label = cmd.Label, SourceType = AgentLabelSourceType.Manual }, ct);
        return Result<AgentLabelDto>.Success(new AgentLabelDto(label.Id, label.AgentId, label.Label, label.SourceType.ToString(), label.CreatedAt));
    }
}
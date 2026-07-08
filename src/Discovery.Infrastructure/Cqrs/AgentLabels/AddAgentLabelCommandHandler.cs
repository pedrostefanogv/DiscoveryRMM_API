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
        var label = await svc.AddAsync(new AgentLabel { AgentId = cmd.AgentId, Label = cmd.Label, SourceType = AgentLabelSourceType.Manual }, ct);
        return Result<AgentLabelDto>.Success(new AgentLabelDto(label.Id, label.AgentId, label.Label, label.SourceType.ToString(), label.CreatedAt));
    }
}
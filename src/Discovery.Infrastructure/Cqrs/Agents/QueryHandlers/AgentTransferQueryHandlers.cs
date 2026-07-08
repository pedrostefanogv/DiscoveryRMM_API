using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Transfer.Commands;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class ValidateAgentTransferQueryHandler(
    IAgentTransferService transferService
) : IRequestHandler<ValidateAgentTransferQuery, Result<AgentTransferDto>>
{
    public async Task<Result<AgentTransferDto>> Handle(ValidateAgentTransferQuery q, CancellationToken ct)
    {
        var validation = await transferService.ValidateAsync(q.AgentId, q.TargetSiteId, Guid.Empty, ct);
        return Result<AgentTransferDto>.Success(new AgentTransferDto(
            validation.IsValid,
            validation.Messages.Count > 0 ? string.Join("; ", validation.Messages) : null,
            validation.IsValid ? q.TargetSiteId : null));
    }
}
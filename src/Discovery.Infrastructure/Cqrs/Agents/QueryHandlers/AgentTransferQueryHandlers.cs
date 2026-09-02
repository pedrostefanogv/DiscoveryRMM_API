using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Transfer.Commands;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class ValidateAgentTransferQueryHandler(
    IAgentTransferService transferService
) : IRequestHandler<ValidateAgentTransferQuery, Result<ValidateTransferResponseDto>>
{
    public async Task<Result<ValidateTransferResponseDto>> Handle(ValidateAgentTransferQuery q, CancellationToken ct)
    {
        var validation = await transferService.ValidateAsync(q.AgentId, q.TargetSiteId, Guid.Empty, ct);

        var dto = new ValidateTransferResponseDto(
            IsValid: validation.IsValid,
            Messages: validation.Messages,
            IsCrossClient: validation.IsCrossClient,
            PreviousSiteName: validation.PreviousSiteName,
            TargetSiteName: validation.TargetSiteName,
            PreviousClientName: validation.PreviousClientName,
            TargetClientName: validation.TargetClientName
        );

        return Result<ValidateTransferResponseDto>.Success(dto);
    }
}
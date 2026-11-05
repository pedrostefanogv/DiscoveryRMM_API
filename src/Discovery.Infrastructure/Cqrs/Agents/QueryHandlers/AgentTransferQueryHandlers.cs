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
        // A regra de permissão tem de ser avaliada para o usuário autenticado. Passar
        // Guid.Empty (bug anterior) fazia HasPermissionAsync consultar um usuário sem
        // nenhuma atribuição e negar sempre — qualquer usuário via "User does not have
        // Edit permission on the source site.", em qualquer escopo.
        if (q.UserId == Guid.Empty)
            return Result<ValidateTransferResponseDto>.Failure(Error.Unauthorized("User not authenticated."));

        var validation = await transferService.ValidateAsync(q.AgentId, q.TargetSiteId, q.UserId, ct);

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
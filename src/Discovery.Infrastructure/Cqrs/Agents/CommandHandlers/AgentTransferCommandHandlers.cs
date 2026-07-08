using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Transfer.Commands;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class TransferAgentCommandHandler(
    IAgentTransferService transferService
) : IRequestHandler<TransferAgentCommand, Result<AgentTransferDto>>
{
    public async Task<Result<AgentTransferDto>> Handle(TransferAgentCommand cmd, CancellationToken ct)
    {
        try
        {
            var result = await transferService.TransferAsync(cmd.AgentId, cmd.TargetSiteId, cmd.UserId, null, ct);
            return Result<AgentTransferDto>.Success(new AgentTransferDto(true, null, result.Agent.SiteId));
        }
        catch (InvalidOperationException ex)
        {
            return Result<AgentTransferDto>.Failure(Error.Validation("Transfer", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<AgentTransferDto>.Failure(Error.Forbidden(ex.Message));
        }
    }
}

public sealed class BulkTransferAgentsCommandHandler(
    IAgentTransferService transferService
) : IRequestHandler<BulkTransferAgentsCommand, Result<AgentTransferDto>>
{
    public async Task<Result<AgentTransferDto>> Handle(BulkTransferAgentsCommand cmd, CancellationToken ct)
    {
        var result = await transferService.BulkTransferAsync(cmd.AgentIds, cmd.TargetSiteId, cmd.UserId, null, ct);
        return Result<AgentTransferDto>.Success(new AgentTransferDto(
            result.ErrorCount == 0,
            result.ErrorCount > 0 ? $"{result.SuccessCount} succeeded, {result.ErrorCount} failed" : null,
            cmd.TargetSiteId));
    }
}
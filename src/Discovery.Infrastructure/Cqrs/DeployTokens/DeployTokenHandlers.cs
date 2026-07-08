using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.DeployTokens.Commands;
using Discovery.Core.Cqrs.DeployTokens.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.DeployTokens;

public sealed class ListDeployTokensQueryHandler(IDeployTokenRepository repo) : IRequestHandler<ListDeployTokensQuery, Result<IReadOnlyList<DeployTokenDto>>>
{
    public async Task<Result<IReadOnlyList<DeployTokenDto>>> Handle(ListDeployTokensQuery q, CancellationToken ct)
    {
        var tokens = await repo.GetByClientSiteAsync(q.ClientId, q.SiteId);
        var items = tokens.Select(t => new DeployTokenDto(t.Id, t.ClientId, t.SiteId, t.TokenPrefix, t.Description, t.CreatedAt, t.ExpiresAt, t.IsRevoked, t.IsExpired, t.UsedCount)).ToList().AsReadOnly();
        return Result<IReadOnlyList<DeployTokenDto>>.Success(items);
    }
}

public sealed class CreateDeployTokenCommandHandler(IDeployTokenService svc) : IRequestHandler<CreateDeployTokenCommand, Result<DeployTokenDto>>
{
    public async Task<Result<DeployTokenDto>> Handle(CreateDeployTokenCommand cmd, CancellationToken ct)
    {
        var (token, _) = await svc.CreateTokenAsync(cmd.ClientId, cmd.SiteId, cmd.Description, cmd.ExpiresInHours, cmd.MultiUse);
        return Result<DeployTokenDto>.Success(new DeployTokenDto(token.Id, token.ClientId, token.SiteId, token.TokenPrefix, token.Description, token.CreatedAt, token.ExpiresAt, token.IsRevoked, token.IsExpired, 0));
    }
}

public sealed class RevokeDeployTokenCommandHandler(IDeployTokenService svc) : IRequestHandler<RevokeDeployTokenCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RevokeDeployTokenCommand cmd, CancellationToken ct)
    {
        await svc.RevokeTokenAsync(cmd.TokenId);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

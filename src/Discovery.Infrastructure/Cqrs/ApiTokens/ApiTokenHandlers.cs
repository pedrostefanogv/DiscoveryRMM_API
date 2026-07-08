using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.ApiTokens.Commands;
using Discovery.Core.Cqrs.ApiTokens.Queries;
using Discovery.Core.Interfaces.Auth;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.ApiTokens;

public sealed class ListApiTokensQueryHandler(IApiTokenService svc) : IRequestHandler<ListApiTokensQuery, Result<IReadOnlyList<ApiTokenDto>>>
{
    public async Task<Result<IReadOnlyList<ApiTokenDto>>> Handle(ListApiTokensQuery q, CancellationToken ct)
    {
        var tokens = await svc.GetByUserAsync(q.UserId);
        var items = tokens.Select(t => new ApiTokenDto(t.Id, t.Name, t.TokenIdPublic, t.IsActive, t.CreatedAt, t.ExpiresAt, t.LastUsedAt)).ToList().AsReadOnly();
        return Result<IReadOnlyList<ApiTokenDto>>.Success(items);
    }
}

public sealed class CreateApiTokenCommandHandler(IApiTokenService svc) : IRequestHandler<CreateApiTokenCommand, Result<ApiTokenDto>>
{
    public async Task<Result<ApiTokenDto>> Handle(CreateApiTokenCommand cmd, CancellationToken ct)
    {
        var result = await svc.CreateTokenAsync(cmd.UserId, cmd.Name, cmd.ExpiresAt);
        return Result<ApiTokenDto>.Success(new ApiTokenDto(result.Id, cmd.Name, result.TokenIdPublic, true, DateTime.UtcNow, cmd.ExpiresAt, null));
    }
}

public sealed class RevokeApiTokenCommandHandler(IApiTokenService svc) : IRequestHandler<RevokeApiTokenCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RevokeApiTokenCommand cmd, CancellationToken ct)
    {
        var ok = await svc.RevokeAsync(cmd.TokenId, cmd.UserId);
        return ok ? Result<VoidResult>.Success(VoidResult.Value) : Result<VoidResult>.Failure(Error.NotFound("Token not found"));
    }
}

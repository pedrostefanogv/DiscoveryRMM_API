using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Mfa.Queries;
using Discovery.Core.Interfaces.Security;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Mfa;

public sealed class ListMfaKeysQueryHandler(IUserMfaKeyRepository repo) : IRequestHandler<ListMfaKeysQuery, Result<IReadOnlyList<MfaKeyDto>>>
{
    public async Task<Result<IReadOnlyList<MfaKeyDto>>> Handle(ListMfaKeysQuery q, CancellationToken ct)
    {
        var keys = await repo.GetActiveByUserIdAsync(q.UserId);
        var items = keys.Select(k => new MfaKeyDto(k.Id, k.UserId, k.KeyType.ToString(), k.Name, k.IsActive, k.CreatedAt, k.LastUsedAt)).ToList().AsReadOnly();
        return Result<IReadOnlyList<MfaKeyDto>>.Success(items);
    }
}

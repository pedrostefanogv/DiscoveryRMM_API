using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Mfa.Queries;

public sealed record ListMfaKeysQuery(Guid UserId) : IQuery<Result<IReadOnlyList<MfaKeyDto>>>;
public sealed record MfaKeyDto(Guid Id, Guid UserId, string KeyType, string Name, bool IsActive, DateTime CreatedAt, DateTime? LastUsedAt);

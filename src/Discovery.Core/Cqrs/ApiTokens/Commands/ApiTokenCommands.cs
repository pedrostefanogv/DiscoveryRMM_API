using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.ApiTokens.Commands;

public sealed record CreateApiTokenCommand(Guid UserId, string Name, DateTime? ExpiresAt)
    : ICommand<Result<ApiTokenDto>>;
public sealed record RevokeApiTokenCommand(Guid TokenId, Guid UserId) : ICommand<Result<VoidResult>>;
public sealed record ApiTokenDto(Guid Id, string Name, string TokenIdPublic, bool IsActive, DateTime CreatedAt, DateTime? ExpiresAt, DateTime? LastUsedAt);
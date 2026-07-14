using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.DeployTokens.Commands;

public sealed record CreateDeployTokenCommand(
    Guid ClientId,
    Guid SiteId,
    string? Description,
    int? ExpiresInHours,
    bool MultiUse,
    string? Delivery = null  // "token" | "installer" | "full-installer" | null
) : ICommand<Result<DeployTokenDto>>;
public sealed record RevokeDeployTokenCommand(Guid TokenId) : ICommand<Result<VoidResult>>;
public sealed record DeployTokenDto(Guid Id, Guid? ClientId, Guid? SiteId, string TokenPrefix, string? RawToken, string? Description, DateTime CreatedAt, DateTime? ExpiresAt, bool IsRevoked, bool IsExpired, int UsedCount);

using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Users.Commands;

public sealed record CreateUserCommand(
    string Login, string Email, string FullName, string Password
) : ICommand<Result<UserDto>>;

public sealed record UpdateUserCommand(
    Guid Id, string? Login, string? Email, string? FullName,
    bool? IsActive, bool? MfaRequired
) : ICommand<Result<UserDto>>;

public sealed record DeleteUserCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record UserDto(
    Guid Id, string Login, string Email, string FullName,
    bool IsActive, bool MfaRequired, bool MfaConfigured,
    bool MustChangePassword, bool MustChangeProfile,
    int FailedLoginAttempts, DateTime? LockoutUntil,
    DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastLoginAt
);

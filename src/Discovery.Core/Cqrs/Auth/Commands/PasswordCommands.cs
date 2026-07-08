using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Auth.Commands;

/// <summary>
/// Command to reset a user's password.
/// </summary>
public sealed record ResetUserPasswordCommand(
    Guid UserId,
    string NewPassword,
    string? RequestedBy
) : ICommand<Result<VoidResult>>;

/// <summary>
/// Command to change own password (requires current password).
/// </summary>
public sealed record ChangeUserPasswordCommand(
    Guid UserId,
    string CurrentPassword,
    string NewPassword
) : ICommand<Result<VoidResult>>;

/// <summary>
/// Command to validate OTP token.
/// </summary>
public sealed record ValidateOtpCommand(
    Guid UserId,
    string OtpCode
) : ICommand<Result<ValidateOtpResult>>;

public sealed record ValidateOtpResult(
    bool IsValid,
    string? Token
);


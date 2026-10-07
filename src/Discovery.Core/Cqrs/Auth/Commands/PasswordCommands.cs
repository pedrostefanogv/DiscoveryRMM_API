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
    string NewPassword,
    /// <summary>Sessão atual (claim jti) — preservada; as demais são revogadas.</summary>
    string? CurrentSessionId = null
) : ICommand<Result<VoidResult>>;

// ValidateOtpCommand/ValidateOtpResult foram removidos: o handler era um stub que
// sempre falhava ("requires full user context") e nada o consumia — o fluxo real de
// OTP vive em CompleteOtpAssertionCommandHandler.


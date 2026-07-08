using Discovery.Core.Cqrs;
using Discovery.Core.DTOs.Auth;

namespace Discovery.Core.Cqrs.Auth.Commands;

public sealed record CompleteFido2AssertionCommand(
    Guid UserId,
    string AssertionResponseJson,
    string? IpAddress,
    string? UserAgent
) : ICommand<Result<TokenPairDto>>;

public sealed record CompleteOtpAssertionCommand(
    Guid UserId,
    string Code,
    string? IpAddress,
    string? UserAgent
) : ICommand<Result<TokenPairDto>>;

public sealed record CompleteFirstAccessCommand(
    Guid UserId,
    CompleteFirstAccessRequestDto Dto
) : ICommand<Result<VoidResult>>;

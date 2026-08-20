using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Mfa;

public sealed record BeginFido2RegistrationQuery(Guid UserId) : IQuery<Result<BeginFido2RegistrationResult>>;
public sealed record BeginFido2RegistrationResult(string OptionsJson);

public sealed record CompleteFido2RegistrationCommand(
    Guid UserId,
    string AttestationResponseJson,
    string KeyName
) : ICommand<Result<CompleteFido2RegistrationResult>>;

public sealed record CompleteFido2RegistrationResult(Guid KeyId, string Message);

public sealed record RenameMfaKeyCommand(
    Guid KeyId,
    Guid UserId,
    string KeyName
) : ICommand<Result<VoidResult>>;

public sealed record DeleteMfaKeyCommand(
    Guid KeyId,
    Guid UserId
) : ICommand<Result<VoidResult>>;
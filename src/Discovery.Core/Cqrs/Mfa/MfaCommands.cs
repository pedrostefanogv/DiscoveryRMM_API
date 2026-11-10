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

public sealed record BeginTotpRegistrationQuery(Guid UserId) : IQuery<Result<BeginTotpRegistrationResult>>;
public sealed record BeginTotpRegistrationResult(string SecretBase32, string QrCodeUri, string Message);

public sealed record CompleteTotpRegistrationCommand(
    Guid UserId,
    string SecretBase32,
    string VerificationCode,
    string KeyName
) : ICommand<Result<CompleteTotpRegistrationResult>>;

public sealed record CompleteTotpRegistrationResult(string Message, IReadOnlyList<string> BackupCodes);

public sealed record RenameMfaKeyCommand(
    Guid KeyId,
    Guid UserId,
    string KeyName
) : ICommand<Result<VoidResult>>;

public sealed record DeleteMfaKeyCommand(
    Guid KeyId,
    Guid UserId
) : ICommand<Result<VoidResult>>;

/// <summary>
/// Telemetria da falha WebAuthn ocorrida no CLIENTE (DOMException). A investigação do Android
/// mostrou que a maioria das falhas acontece no navegador antes de chegar ao servidor — sem
/// este canal o caso simplesmente não aparece em log nenhum.
/// </summary>
public sealed record ReportFido2ClientErrorCommand(
    Guid UserId,
    string? ErrorName,
    string? ErrorMessage,
    string? Stage,
    string? UserAgent
) : ICommand<Result<VoidResult>>;
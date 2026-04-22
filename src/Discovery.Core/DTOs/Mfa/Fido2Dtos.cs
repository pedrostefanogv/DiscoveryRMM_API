namespace Discovery.Core.DTOs.Mfa;

/// <summary>Resultado de registro FIDO2 bem-sucedido.</summary>
public class Fido2CredentialResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string CredentialIdBase64 { get; set; } = string.Empty;
    public string PublicKeyBase64 { get; set; } = string.Empty;
    public uint SignCount { get; set; }
    public string? AaguidBase64 { get; set; }
    public string? UserHandleBase64 { get; set; }

    public static Fido2CredentialResult Fail(string error) =>
        new() { Success = false, ErrorMessage = error };
}

/// <summary>Resultado de assertion (verificação) FIDO2 bem-sucedida.</summary>
public class Fido2AssertionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>Id (PK) da UserMfaKey que foi autenticada.</summary>
    public Guid KeyId { get; set; }
    public string CredentialIdBase64 { get; set; } = string.Empty;
    public uint NewSignCount { get; set; }

    public static Fido2AssertionResult Fail(string error) =>
        new() { Success = false, ErrorMessage = error };
}

/// <summary>Corpo enviado pelo cliente ao completar o registro FIDO2.</summary>
public class CompleteFido2RegistrationDto
{
    /// <summary>JSON serializado do AuthenticatorAttestationRawResponse.</summary>
    public string AttestationResponseJson { get; set; } = string.Empty;
    /// <summary>Nome amigável para a chave (ex.: "YubiKey 5C").</summary>
    public string KeyName { get; set; } = string.Empty;
}

/// <summary>Corpo enviado pelo cliente ao completar a assertion FIDO2.</summary>
public class CompleteFido2AssertionDto
{
    /// <summary>JSON serializado do AuthenticatorAssertionRawResponse.</summary>
    public string AssertionResponseJson { get; set; } = string.Empty;
}

/// <summary>Corpo enviado pelo cliente ao concluir cadastro de TOTP.</summary>
public class CompleteTotpRegistrationDto
{
    /// <summary>Segredo TOTP Base32 retornado no endpoint de begin.</summary>
    public string SecretBase32 { get; set; } = string.Empty;

    /// <summary>Código atual do app autenticador para validação do setup.</summary>
    public string VerificationCode { get; set; } = string.Empty;

    /// <summary>Nome amigável para a credencial OTP.</summary>
    public string KeyName { get; set; } = string.Empty;
}

/// <summary>Corpo enviado para concluir MFA via OTP durante login.</summary>
public class CompleteOtpAssertionDto
{
    /// <summary>Código TOTP de 6 dígitos.</summary>
    public string Code { get; set; } = string.Empty;
}

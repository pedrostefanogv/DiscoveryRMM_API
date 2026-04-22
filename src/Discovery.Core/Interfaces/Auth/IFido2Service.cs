using Discovery.Core.DTOs.Mfa;
using Discovery.Core.Entities.Security;

namespace Discovery.Core.Interfaces.Auth;

public interface IFido2Service
{
    /// <summary>
    /// Inicia registro de nova chave FIDO2 para o usuário.
    /// Armazena o challenge no Redis (TTL 2 min).
    /// Retorna o JSON de CredentialCreateOptions para ser passado ao cliente.
    /// </summary>
    Task<string> BeginRegistrationAsync(Guid userId, string email, string fullName, IEnumerable<string> existingCredentialIds);

    /// <summary>
    /// Completa o registro FIDO2.
    /// Valida a resposta do autenticador contra o challenge armazenado.
    /// Retorna os dados da credencial para persistir.
    /// </summary>
    Task<Fido2CredentialResult> CompleteRegistrationAsync(Guid userId, string attestationResponseJson);

    /// <summary>
    /// Inicia assertion (login) FIDO2.
    /// Armazena o challenge no Redis.
    /// Retorna o JSON de AssertionOptions.
    /// </summary>
    Task<string> BeginAssertionAsync(Guid userId, IEnumerable<UserMfaKey> activeKeys);

    /// <summary>
    /// Completa a assertion FIDO2.
    /// Retorna o keyId que foi usado e o novo signCount.
    /// </summary>
    Task<Fido2AssertionResult> CompleteAssertionAsync(Guid userId, string assertionResponseJson, IEnumerable<UserMfaKey> activeKeys);
}

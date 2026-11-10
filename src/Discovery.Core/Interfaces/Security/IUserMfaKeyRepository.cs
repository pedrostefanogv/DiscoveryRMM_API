using Discovery.Core.Entities.Security;

namespace Discovery.Core.Interfaces.Security;

public interface IUserMfaKeyRepository
{
    Task<UserMfaKey?> GetByIdAsync(Guid id);
    Task<IEnumerable<UserMfaKey>> GetActiveByUserIdAsync(Guid userId);
    Task<UserMfaKey?> GetByCredentialIdAsync(string credentialIdBase64);
    Task<UserMfaKey> CreateAsync(UserMfaKey key);
    Task<bool> UpdateSignCountAsync(Guid keyId, uint newSignCount);
    Task<bool> UpdateLastUsedAsync(Guid keyId);

    /// <summary>
    /// Regrava os dados FIDO2 de uma credencial já existente (inclusive o credential id, para
    /// normalizá-lo em base64url) e os transports. Usado quando o mesmo autenticador conclui o
    /// registro mais de uma vez.
    /// </summary>
    Task<bool> UpdateFido2KeyAsync(
        Guid keyId,
        string credentialIdBase64,
        string publicKeyBase64,
        uint signCount,
        string? aaguidBase64,
        string? userHandleBase64,
        string? transports);

    /// <summary>Marca o passo TOTP consumido (anti-replay do mesmo código).</summary>
    Task<bool> SetLastUsedStepAsync(Guid keyId, long step);

    /// <summary>Atualiza os hashes dos códigos de backup (consumo de uso único).</summary>
    Task<bool> UpdateBackupCodeHashesAsync(Guid keyId, string[] hashes);
    Task<bool> DeactivateAsync(Guid keyId, Guid userId);
    Task<int> DeactivateAllByUserIdAsync(Guid userId);
    Task<int> CountActiveByUserIdAsync(Guid userId);
    Task<bool> RenameAsync(Guid keyId, Guid userId, string newName);
}

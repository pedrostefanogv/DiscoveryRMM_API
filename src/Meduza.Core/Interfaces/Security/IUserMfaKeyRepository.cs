using Meduza.Core.Entities.Security;

namespace Meduza.Core.Interfaces.Security;

public interface IUserMfaKeyRepository
{
    Task<UserMfaKey?> GetByIdAsync(Guid id);
    Task<IEnumerable<UserMfaKey>> GetActiveByUserIdAsync(Guid userId);
    Task<UserMfaKey?> GetByCredentialIdAsync(string credentialIdBase64);
    Task<UserMfaKey> CreateAsync(UserMfaKey key);
    Task<bool> UpdateSignCountAsync(Guid keyId, uint newSignCount);
    Task<bool> UpdateLastUsedAsync(Guid keyId);
    Task<bool> DeactivateAsync(Guid keyId, Guid userId);
    Task<int> DeactivateAllByUserIdAsync(Guid userId);
    Task<int> CountActiveByUserIdAsync(Guid userId);
    Task<bool> RenameAsync(Guid keyId, Guid userId, string newName);
}

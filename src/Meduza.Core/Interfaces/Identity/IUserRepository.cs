using Meduza.Core.Entities.Identity;

namespace Meduza.Core.Interfaces.Identity;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id);
    Task<User?> GetByLoginAsync(string login);
    Task<User?> GetByEmailAsync(string email);
    /// <summary>Busca por login OU email (para o fluxo de login).</summary>
    Task<User?> GetByLoginOrEmailAsync(string loginOrEmail);
    Task<IEnumerable<User>> GetAllAsync(int skip = 0, int take = 50);
    Task<User> CreateAsync(User user);
    Task<User> UpdateAsync(User user);
    Task<bool> SetMfaConfiguredAsync(Guid userId, bool configured);
    Task<bool> SetLastLoginAsync(Guid userId, DateTime at);
    Task<bool> ExistsByLoginAsync(string login);
    Task<bool> ExistsByEmailAsync(string email);
    Task<int> CountAsync();
}

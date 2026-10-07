using Discovery.Core.Entities.Identity;

namespace Discovery.Core.Interfaces.Identity;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id);

    /// <summary>Busca vários usuários por id em uma única query (evita N+1 em listagens).</summary>
    Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> ids);
    Task<User?> GetByLoginAsync(string login);
    Task<User?> GetByEmailAsync(string email);
    /// <summary>Busca por login OU email (para o fluxo de login).</summary>
    Task<User?> GetByLoginOrEmailAsync(string loginOrEmail);
    Task<IReadOnlyList<User>> GetAllPageAsync(string? cursor, int take = 50);
    Task<User> CreateAsync(User user);
    Task<User> UpdateAsync(User user);
    Task<bool> SetMfaConfiguredAsync(Guid userId, bool configured);
    Task<bool> SetLastLoginAsync(Guid userId, DateTime at);

    /// <summary>
    /// Atualiza apenas os contadores de MFA (evita regravar a linha inteira do usuário,
    /// que causava lost update quando outra alteração concorria com o registro da falha).
    /// </summary>
    Task<bool> SetMfaFailureStateAsync(Guid userId, int failedAttempts, DateTime? lockoutUntil);

    /// <summary>Zera os contadores de MFA após um segundo fator válido.</summary>
    Task<bool> ResetMfaFailureStateAsync(Guid userId);
    Task<bool> ExistsByLoginAsync(string login);
    Task<bool> ExistsByEmailAsync(string email);
    Task<int> CountAsync();
}

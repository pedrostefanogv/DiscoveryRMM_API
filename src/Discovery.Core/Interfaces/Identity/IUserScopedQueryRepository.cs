using Discovery.Core.Entities.Identity;

namespace Discovery.Core.Interfaces.Identity;

/// <summary>
/// Listagem de usuários com row-level security (escopo do ator).
///
/// Interface própria (e não mais dois métodos em <see cref="IUserRepository"/>)
/// porque a listagem escopada é uma responsabilidade distinta das operações de
/// CRUD do repositório — e adicionar membros à interface existente quebraria
/// todos os dublês de teste sem necessidade.
/// </summary>
public interface IUserScopedQueryRepository
{
    /// <summary>
    /// Página de usuários visível ao ator.
    ///
    /// <paramref name="allowedClientIds"/>/<paramref name="allowedSiteIds"/> só são
    /// aplicados quando <paramref name="hasGlobalAccess"/> é falso. A regra é
    /// "usuário que possui ao menos uma atribuição de papel dentro do escopo":
    /// sem ela, um administrador de cliente veria TODOS os usuários da plataforma.
    /// </summary>
    Task<IReadOnlyList<User>> GetScopedPageAsync(
        bool hasGlobalAccess,
        IReadOnlyList<Guid> allowedClientIds,
        IReadOnlyList<Guid> allowedSiteIds,
        string? cursor,
        int take = 50);

    /// <summary>Total de usuários visíveis no escopo (para o Total da página).</summary>
    Task<int> CountScopedAsync(
        bool hasGlobalAccess,
        IReadOnlyList<Guid> allowedClientIds,
        IReadOnlyList<Guid> allowedSiteIds);

    /// <summary>
    /// True quando o usuário alvo está DENTRO do escopo do ator — usado para
    /// autorizar operações direcionadas (ler, editar, desativar, resetar senha,
    /// revogar MFA de um usuário específico).
    ///
    /// Sem esta checagem, trocar os controllers de identidade para
    /// <c>ScopeSource.AccessList</c> abriria um buraco pior do que o fechado: um
    /// administrador escopado ao cliente A poderia ler/alterar QUALQUER usuário da
    /// plataforma (inclusive de outros clientes e administradores globais) bastando
    /// conhecer o id.
    ///
    /// Regra de visibilidade idêntica à da listagem: o alvo precisa ter ao menos uma
    /// atribuição de papel cujo escopo esteja contido no escopo do ator. Com acesso
    /// global, qualquer alvo é permitido.
    /// </summary>
    Task<bool> IsUserInScopeAsync(
        bool hasGlobalAccess,
        IReadOnlyList<Guid> allowedClientIds,
        IReadOnlyList<Guid> allowedSiteIds,
        Guid targetUserId);
}

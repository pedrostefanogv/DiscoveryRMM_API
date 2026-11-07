using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;

namespace Discovery.Infrastructure.Services.Identity;

/// <summary>
/// Regra de "quem pode conceder o quê" na atribuição de papel a um grupo.
///
/// Sem esta regra, <c>POST /user-groups/{id}/roles</c> aceitava qualquer payload
/// desde que o chamador tivesse <c>Users:Edit</c> em algum escopo. O payload mais
/// barato de escalação era <c>{ roleId: &lt;papel de admin&gt;, scopeLevel: "Global" }</c>,
/// que elevava o concedente (ou um cúmplice) a administrador de toda a plataforma.
///
/// A v1 aplica a regra POR ESCOPO (decisão do dono do produto): o concedente só
/// pode conceder escopo contido no próprio. O endurecimento por subconjunto de
/// permissões fica para uma fase posterior.
///
/// Fica isolada para ser testável com fakes de <see cref="UserScopeAccess"/>.
/// </summary>
public static class RoleGrantScopePolicy
{
    /// <summary>
    /// True quando o ator pode conceder uma atribuição no escopo informado.
    ///
    /// - Global exige acesso global (um admin de cliente NUNCA concede Global).
    /// - Client exige aquele cliente (ou acesso global).
    /// - Site exige aquele site, o cliente pai do site (quando informado) ou acesso global.
    /// </summary>
    /// <param name="granterAccess">Escopo efetivo do ator para Users.Edit.</param>
    /// <param name="scopeLevel">Nível do escopo sendo concedido.</param>
    /// <param name="scopeId">Cliente/site concedido (null quando Global).</param>
    /// <param name="parentClientId">
    /// Cliente pai do site, quando o ator concede num Site. Necessário porque uma
    /// atribuição de Client também autoriza operar nos sites daquele cliente.
    /// </param>
    public static bool CanGrant(
        UserScopeAccess? granterAccess,
        ScopeLevel scopeLevel,
        Guid? scopeId,
        Guid? parentClientId = null)
    {
        if (granterAccess is null)
            return false;

        if (granterAccess.HasGlobalAccess)
            return true;

        switch (scopeLevel)
        {
            case ScopeLevel.Global:
                // Sem acesso global, ninguém promove a global — inclui o próprio admin
                // de cliente tentando se elevar.
                return false;

            case ScopeLevel.Client:
                return scopeId.HasValue
                    && scopeId.Value != Guid.Empty
                    && granterAccess.AllowedClientIds.Contains(scopeId.Value);

            case ScopeLevel.Site:
                if (!scopeId.HasValue || scopeId.Value == Guid.Empty)
                    return false;

                if (granterAccess.AllowedSiteIds.Contains(scopeId.Value))
                    return true;

                // Conceder num site do próprio cliente é permitido: a atribuição de
                // Client já abrange todos os sites daquele cliente.
                return parentClientId.HasValue
                    && parentClientId.Value != Guid.Empty
                    && granterAccess.AllowedClientIds.Contains(parentClientId.Value);

            default:
                return false;
        }
    }
}

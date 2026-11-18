using Discovery.Core.Interfaces.Auth;

namespace Discovery.Core.Helpers;

/// <summary>
/// Decisão de autorização scoped (client/site) reutilizável por handlers de
/// agentes, tickets e afins.
///
/// O atributo <c>RequirePermission(..., ScopeSource.Global)</c> só garante que o
/// usuário possui a permissão em ALGUM lugar — ele NÃO prova que o recurso alvo
/// pertence ao cliente/site do usuário. Endpoints de LISTAGEM/FILTRADO devem
/// aceitar usuário escopado (AccessList no filtro) e recortar o tenant aqui,
/// devolvendo Forbidden/NotFound em vez de 403 do filtro.
///
/// Global passa direto; atribuição de Client vale para o cliente do recurso;
/// atribuição de Site vale apenas para o site exato.
/// </summary>
public static class TenantScopePolicy
{
    public static bool Allows(UserScopeAccess? access, Guid clientId, Guid siteId)
    {
        if (access is null)
            return false;

        if (access.HasGlobalAccess)
            return true;

        // Guid.Empty nunca casa com uma lista de escopos (recurso não resolvido
        // não pode virar "acesso liberado").
        if (clientId != Guid.Empty && access.AllowedClientIds.Contains(clientId))
            return true;

        return siteId != Guid.Empty && access.AllowedSiteIds.Contains(siteId);
    }
}

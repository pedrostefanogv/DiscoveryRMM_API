using Discovery.Core.Interfaces.Auth;

namespace Discovery.Api.Services;

/// <summary>
/// Decisão de autorização scoped de uma sessão/acesso remoto por agente.
///
/// O atributo <c>RequirePermission(..., ScopeSource.Global)</c> só garante que o
/// usuário possui a permissão Agents.View/Execute em ALGUM lugar — ele NÃO prova
/// que o agente alvo pertence ao cliente/site do usuário. Foi exatamente essa
/// lacuna que permitia a um técnico escopado a um cliente abrir sessão remota em
/// qualquer máquina do tenant (start, controles e listagem de sessões ativas).
///
/// Esta policy é quem recorta o tenant. Fica isolada do controller/handler para
/// poder ser testada com fakes de <see cref="UserScopeAccess"/>, no mesmo padrão
/// de <c>TerminalRecordingAccessPolicy</c>.
/// </summary>
public static class RemoteSessionScopePolicy
{
    /// <summary>
    /// True quando o usuário pode operar acesso remoto no agente informado.
    ///
    /// Global passa direto; atribuição de Client vale para o cliente do agente;
    /// atribuição de Site vale apenas para o site exato do agente. A herança
    /// Client -> Site já é aplicada pelo <c>IPermissionService</c> (uma atribuição
    /// de Client já aparece em AllowedClientIds), então aqui basta comparar.
    /// </summary>
    /// <param name="access">Escopo efetivo do usuário para Agents+View/Execute.</param>
    /// <param name="agentClientId">Cliente do agente alvo (derivado do Site do agente).</param>
    /// <param name="agentSiteId">Site do agente alvo.</param>
    public static bool Allows(UserScopeAccess? access, Guid agentClientId, Guid agentSiteId)
    {
        if (access is null)
            return false;

        if (access.HasGlobalAccess)
            return true;

        // Guid.Empty nunca deve casar com uma lista de escopos (site não resolvido
        // não pode virar "acesso liberado"); o mesmo cuidado existe em
        // TerminalRecordingAccessPolicy e é coberto por teste dedicado.
        if (agentClientId != Guid.Empty && access.AllowedClientIds.Contains(agentClientId))
            return true;

        return agentSiteId != Guid.Empty && access.AllowedSiteIds.Contains(agentSiteId);
    }
}

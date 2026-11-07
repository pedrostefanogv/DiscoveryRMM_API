using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;

namespace Discovery.Api.Services;

/// <summary>
/// Resultado da avaliação de escopo de um agente para um usuário.
///
/// Distinguir os casos importa: "não tem a permissão" deve virar 403 (quem
/// responde é o RequirePermission), enquanto "tem permissão, mas não neste
/// cliente/site" deve virar 404 (não confirmar a existência do recurso de outro
/// tenant). Tratar os dois como 404 muda o contrato para quem simplesmente não
/// tem acesso ao recurso.
/// </summary>
public enum RemoteSessionScopeDecision
{
    /// <summary>Escopo permite o acesso ao agente.</summary>
    Allowed,

    /// <summary>Usuário sem nenhum acesso a Agents — o RequirePermission decide.</summary>
    WithoutResourceAccess,

    /// <summary>Tem acesso a Agents, mas não a este cliente/site.</summary>
    DeniedOutOfScope
}

/// <summary>
/// Resolve o escopo (cliente/site) de um agente e decide se o usuário pode
/// operar acesso remoto nele.
///
/// Existe para que o filtro (todos os endpoints de sessão remota) e o handler de
/// start compartilhem UMA única regra — duplicar a comparação já causou
/// divergência em outros pontos deste módulo (ver comentário em
/// RemoteSessionViewerPermissions).
/// </summary>
public sealed class RemoteSessionScopeResolver(
    IAgentRepository agents,
    ISiteRepository sites,
    IScopeContext scopeContext)
{
    /// <summary>
    /// True quando o usuário tem escopo para o agente. Resolve o tenant do agente
    /// (client = cliente do site do agente) e aplica <see cref="RemoteSessionScopePolicy"/>.
    ///
    /// Retorna true também quando a permissão efetiva é global — o caminho normal
    /// do administrador de plataforma, que não deve pagar nenhuma query extra.
    /// </summary>
    public async Task<bool> CanAccessAgentAsync(Guid userId, Guid agentId, bool isExecute, CancellationToken ct = default)
        => await EvaluateAgentAsync(userId, agentId, isExecute, ct) == RemoteSessionScopeDecision.Allowed;

    /// <summary>
    /// Avalia o acesso ao agente distinguindo "sem permissão" de "fora do escopo".
    /// Use esta sobrecarga onde a diferença de status importa (filtros/controllers).
    /// </summary>
    public async Task<RemoteSessionScopeDecision> EvaluateAgentAsync(
        Guid userId, Guid agentId, bool isExecute, CancellationToken ct = default)
    {
        scopeContext.SetUserId(userId);

        var access = await scopeContext.GetAccessAsync(
            Discovery.Core.Enums.Identity.ResourceType.Agents,
            isExecute ? Discovery.Core.Enums.Identity.ActionType.Execute : Discovery.Core.Enums.Identity.ActionType.View);

        if (access.HasGlobalAccess)
            return RemoteSessionScopeDecision.Allowed;

        // Sem QUALQUER escopo concedido o usuário não tem o recurso: quem deve
        // responder é a checagem de permissão (403), não a de escopo (404).
        if (access.AllowedClientIds.Count == 0 && access.AllowedSiteIds.Count == 0)
            return RemoteSessionScopeDecision.WithoutResourceAccess;

        var agent = await agents.GetByIdAsync(agentId);
        if (agent is null)
            return RemoteSessionScopeDecision.DeniedOutOfScope;

        var site = await sites.GetByIdAsync(agent.SiteId);
        if (site is null)
            return RemoteSessionScopeDecision.DeniedOutOfScope;

        return RemoteSessionScopePolicy.Allows(access, site.ClientId, agent.SiteId)
            ? RemoteSessionScopeDecision.Allowed
            : RemoteSessionScopeDecision.DeniedOutOfScope;
    }

    /// <summary>
    /// Resolve apenas o tenant do agente (clientId do site + siteId). Usado pelo
    /// start para derivar tenant/site quando o comando não os traz populados.
    /// Retorna null quando o agente ou o site não existem.
    /// </summary>
    public async Task<(Guid ClientId, Guid SiteId)?> ResolveAgentTenantAsync(Guid agentId)
    {
        var agent = await agents.GetByIdAsync(agentId);
        if (agent is null)
            return null;

        var site = await sites.GetByIdAsync(agent.SiteId);
        if (site is null)
            return null;

        return (site.ClientId, agent.SiteId);
    }
}

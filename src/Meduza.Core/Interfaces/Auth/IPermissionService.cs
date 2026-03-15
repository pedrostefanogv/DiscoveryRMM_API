using Meduza.Core.Enums.Identity;

namespace Meduza.Core.Interfaces.Auth;

public interface IPermissionService
{
    /// <summary>
    /// Verifica se o usuário tem permissão para executar uma ação sobre um recurso.
    /// A herança de escopo é aplicada automaticamente:
    /// - Global: qualquer role com scopeLevel=Global é aceita.
    /// - Client: roles em Global OU Client com ScopeId=clientId.
    /// - Site: roles em Global OU Client com ScopeId=clientId OU Site com ScopeId=siteId.
    /// </summary>
    Task<bool> HasPermissionAsync(
        Guid userId,
        ResourceType resource,
        ActionType action,
        ScopeLevel scopeLevel = ScopeLevel.Global,
        Guid? scopeId = null,
        Guid? parentScopeId = null);

    /// <summary>
    /// Retorna todos os ClientIds/SiteIds que o usuário pode acessar para um determinado recurso+ação.
    /// Útil para queries filtradas por escopo.
    /// </summary>
    Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action);
}

/// <summary>Representa o acesso de escopo de um usuário a um recurso.</summary>
public class UserScopeAccess
{
    public bool HasGlobalAccess { get; init; }
    public IReadOnlyList<Guid> AllowedClientIds { get; init; } = [];
    public IReadOnlyList<Guid> AllowedSiteIds { get; init; } = [];
}

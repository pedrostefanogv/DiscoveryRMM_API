using Discovery.Core.Enums.Identity;

namespace Discovery.Core.Interfaces.Auth;

/// <summary>
/// Provedor de escopo com cache intra-request.
/// Populado automaticamente por RequirePermission (via HttpContext.Items)
/// ou explicitamente via GetAccessAsync para row-level filtering em queries.
/// </summary>
public interface IScopeContext
{
    /// <summary>Resolve e cacheia o escopo de acesso para um recurso+acao.</summary>
    Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action);

    /// <summary>True se o usuario tem acesso global para este recurso, false se precisa filtrar.</summary>
    Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action);

    /// <summary>Define o UserId usado internamente para as resolucoes.</summary>
    void SetUserId(Guid userId);

    /// <summary>ClientId/SiteId resolvidos pela rota (populados pelo RequirePermission).</summary>
    Guid? ResolvedClientId { get; set; }
    Guid? ResolvedSiteId { get; set; }
}

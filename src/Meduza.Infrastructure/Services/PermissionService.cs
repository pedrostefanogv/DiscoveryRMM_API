using Meduza.Core.Enums.Identity;
using Meduza.Core.Interfaces.Auth;
using Meduza.Core.Interfaces.Identity;
namespace Meduza.Infrastructure.Services;

/// <inheritdoc />
public class PermissionService : IPermissionService
{
    private readonly IUserGroupRepository _groups;
    private readonly IRoleRepository _roles;

    public PermissionService(IUserGroupRepository groups, IRoleRepository roles)
    {
        _groups = groups;
        _roles = roles;
    }

    public async Task<bool> HasPermissionAsync(
        Guid userId,
        ResourceType resource,
        ActionType action,
        ScopeLevel scopeLevel = ScopeLevel.Global,
        Guid? scopeId = null,
        Guid? parentScopeId = null)
    {
        // Carrega todas as atribuições de role do usuário (via grupos)
        var assignments = (await _groups.GetRolesForUserAsync(userId)).ToList();

        foreach (var assignment in assignments)
        {
            // Verifica se o escopo é compatível com a solicitação
            if (!IsScopeMatch(assignment.ScopeLevel, assignment.ScopeId, scopeLevel, scopeId, parentScopeId))
                continue;

            // Verifica se a role tem a permissão requisitada
            // Nota: as permissões da role precisam ser carregadas via IRoleRepository.
            // Para não carregar individualmente, usamos o IUserGroupRepository que já
            // pode retornar as permissões como parte do query otimizado.
            if (await HasRolePermissionAsync(assignment.RoleId, resource, action))
                return true;
        }

        return false;
    }

    public async Task<UserScopeAccess> GetScopeAccessAsync(Guid userId, ResourceType resource, ActionType action)
    {
        var assignments = (await _groups.GetRolesForUserAsync(userId)).ToList();

        var hasGlobal = false;
        var clientIds = new List<Guid>();
        var siteIds = new List<Guid>();

        foreach (var assignment in assignments)
        {
            if (!await HasRolePermissionAsync(assignment.RoleId, resource, action))
                continue;

            switch (assignment.ScopeLevel)
            {
                case ScopeLevel.Global:
                    hasGlobal = true;
                    break;
                case ScopeLevel.Client when assignment.ScopeId.HasValue:
                    clientIds.Add(assignment.ScopeId.Value);
                    break;
                case ScopeLevel.Site when assignment.ScopeId.HasValue:
                    siteIds.Add(assignment.ScopeId.Value);
                    break;
            }
        }

        return new UserScopeAccess
        {
            HasGlobalAccess = hasGlobal,
            AllowedClientIds = clientIds.Distinct().ToList(),
            AllowedSiteIds = siteIds.Distinct().ToList()
        };
    }

    private bool IsScopeMatch(
        ScopeLevel assignmentScope,
        Guid? assignmentScopeId,
        ScopeLevel requestedScope,
        Guid? requestedScopeId,
        Guid? parentScopeId)
    {
        // Global assignment cobre qualquer scope
        if (assignmentScope == ScopeLevel.Global)
            return true;

        // Client assignment cobre o mesmo client ou sites desse client
        if (assignmentScope == ScopeLevel.Client && assignmentScopeId.HasValue)
        {
            if (requestedScope == ScopeLevel.Client && requestedScopeId == assignmentScopeId)
                return true;
            // Site dentro do cliente (parentScopeId = clientId do site)
            if (requestedScope == ScopeLevel.Site && parentScopeId == assignmentScopeId)
                return true;
        }

        // Site assignment cobre exatamente o mesmo site
        if (assignmentScope == ScopeLevel.Site && assignmentScopeId.HasValue)
        {
            if (requestedScope == ScopeLevel.Site && requestedScopeId == assignmentScopeId)
                return true;
        }

        return false;
    }

    // Cache simples por request para evitar N+1: usado via Lazy loading futuro
    private readonly Dictionary<Guid, HashSet<(ResourceType, ActionType)>> _permissionCache = new();

    private async Task<bool> HasRolePermissionAsync(Guid roleId, ResourceType resource, ActionType action)
    {
        if (!_permissionCache.TryGetValue(roleId, out var perms))
        {
            var permissions = await _roles.GetPermissionsForRoleAsync(roleId);
            perms = permissions.Select(p => (p.ResourceType, p.ActionType)).ToHashSet();
            _permissionCache[roleId] = perms;
        }
        return perms.Contains((resource, action));
    }

    private async Task<HashSet<(ResourceType, ActionType)>> LoadRolePermissionsAsync(Guid roleId)
    {
        // Implementação mínima - será expandida via IRoleRepository
        // Retorna empty set; a implementação real em UserGroupRepository pode
        // retornar permissions junto com as assignments via JOIN.
        await Task.CompletedTask;
        return [];
    }
}

using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementacao padrao de IScopeContext com cache intra-request por usuario +
/// (resourceType, actionType).
///
/// <b>Por que a chave inclui o usuario (correcao de bug):</b> o cache era
/// <c>Dictionary&lt;(ResourceType, ActionType), UserScopeAccess&gt;</c> — sem o
/// usuario. Como este servico e Scoped (uma instancia por request), um mesmo
/// request que resolvesse escopo para DUAS pessoas servia a segunda com o
/// resultado da primeira, silenciosamente.
///
/// Isso nao era hipotetico: o filtro <c>RequirePermission(..., AccessList)</c>
/// popula o cache para o ATOR autenticado, e os handlers de identidade chamam
/// <c>SetUserId(RequestedByUserId)</c> antes de pedir o mesmo par
/// (Users, Edit). Hoje os dois valores coincidem (ambos vem da sessao), entao o
/// defeito ficou latente — mas bastaria um caller passar outro usuario (auditoria
/// "em nome de", impersonation, um job) para uma decisao de autorizacao ser
/// tomada com o escopo da pessoa errada.
///
/// Incluir o usuario na chave remove a classe inteira de erro sem custo: o numero
/// de usuarios distintos por request e 1 ou 2.
/// </summary>
public class ScopeContext : IScopeContext
{
    private readonly IPermissionService _permissionService;
    private Guid? _userId;

    private readonly Dictionary<(Guid UserId, ResourceType Resource, ActionType Action), UserScopeAccess> _cache = new();

    public Guid? ResolvedClientId { get; set; }
    public Guid? ResolvedSiteId { get; set; }

    public ScopeContext(IPermissionService permissionService)
    {
        _permissionService = permissionService;
    }

    /// <inheritdoc />
    public Guid? UserId => _userId;

    public void SetUserId(Guid userId)
    {
        // Nao limpa o cache: as entradas passam a ser por usuario, entao trocar de
        // usuario apenas direciona para outra chave (antes, trocar de usuario
        // continuava lendo a entrada do usuario anterior).
        _userId = userId;
    }

    public async Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action)
    {
        if (_userId is null)
            return new UserScopeAccess { HasGlobalAccess = false };

        var key = (_userId.Value, resource, action);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var access = await _permissionService.GetScopeAccessAsync(_userId.Value, resource, action);
        _cache[key] = access;
        return access;
    }

    public async Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action)
    {
        var access = await GetAccessAsync(resource, action);
        return access.HasGlobalAccess;
    }
}
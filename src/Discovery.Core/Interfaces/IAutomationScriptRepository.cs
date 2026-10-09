using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAutomationScriptRepository
{
    Task<AutomationScriptDefinition> CreateAsync(AutomationScriptDefinition script);
    Task<AutomationScriptDefinition?> GetByIdAsync(Guid id, bool includeInactive = false);

    /// <summary>
    /// Nomes por id numa única consulta (projeção leve, inclui inativos).
    /// Evita N+1 ao enriquecer listagens de execuções.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(IReadOnlyCollection<Guid> ids);

    /// <summary>
    /// Busca vários scripts por id numa única consulta (evita N+1 no policy-sync
    /// e na prévia de políticas). Respeita <paramref name="includeInactive"/> como GetByIdAsync.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, AutomationScriptDefinition>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        bool includeInactive = false);
    Task<IReadOnlyList<AutomationScriptDefinition>> GetListPageAsync(Guid? clientId, bool activeOnly, string? cursor, int limit);
    Task<int> CountAsync(Guid? clientId, bool activeOnly);
    Task UpdateAsync(AutomationScriptDefinition script);
    Task DeleteAsync(Guid id);
}

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
    Task<IReadOnlyList<AutomationScriptDefinition>> GetListPageAsync(Guid? clientId, bool activeOnly, string? cursor, int limit);
    Task<int> CountAsync(Guid? clientId, bool activeOnly);
    Task UpdateAsync(AutomationScriptDefinition script);
    Task DeleteAsync(Guid id);
}

namespace Discovery.Core.Interfaces;

/// <summary>Total e online de agentes de um site.</summary>
public readonly record struct SiteAgentCount(int Total, int Online);

/// <summary>
/// Contagem de agentes por site em uma única consulta — usada pela lista
/// global de sites para não disparar N+1 no frontend.
/// </summary>
public interface ISiteAgentCountService
{
    Task<IReadOnlyDictionary<Guid, SiteAgentCount>> GetCountsBySiteIdsAsync(
        IReadOnlyCollection<Guid> siteIds,
        CancellationToken ct = default);
}

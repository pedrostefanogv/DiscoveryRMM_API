using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço de busca universal com filtragem por escopo de permissão do usuário.
/// Respeita a hierarquia Global → Client → Site para cada tipo de recurso.
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Executa uma busca textual em múltiplas entidades (Agents, Clients, Sites, Tickets, Software),
    /// filtrando os resultados conforme as permissões de acesso do usuário.
    /// </summary>
    /// <param name="userId">ID do usuário logado.</param>
    /// <param name="query">Termo de busca (texto livre).</param>
    /// <param name="maxResults">Máximo de resultados por grupo de entidade.</param>
    /// <param name="ct">Token de cancelamento.</param>
    Task<UniversalSearchResult> SearchAsync(
        Guid userId,
        string query,
        int maxResults = 10,
        CancellationToken ct = default);
}

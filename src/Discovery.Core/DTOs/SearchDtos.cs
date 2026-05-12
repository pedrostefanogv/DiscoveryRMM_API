namespace Discovery.Core.DTOs;

/// <summary>
/// Resultado agregado da busca universal.
/// Contém grupos de resultados por tipo de entidade, respeitando o escopo de acesso do usuário.
/// </summary>
public record UniversalSearchResult(
    List<SearchResultGroup> Groups,
    int TotalResults,
    DateTime GeneratedAtUtc);

/// <summary>
/// Grupo de resultados de um mesmo tipo de entidade.
/// </summary>
public record SearchResultGroup(
    string EntityType,       // "agents", "clients", "sites", "tickets", "software"
    string Label,            // "Agentes", "Clientes", "Sites", "Chamados", "Softwares"
    string Icon,             // Nome do ícone para o frontend
    List<SearchResultItem> Items);

/// <summary>
/// Item individual de resultado da busca.
/// </summary>
public record SearchResultItem(
    Guid Id,
    string Title,
    string? Subtitle,
    string? Description,
    string EntityType,
    Guid? ClientId,
    string? ClientName,
    Guid? SiteId,
    string? SiteName,
    string? Url);            // URL relativa para navegação no frontend

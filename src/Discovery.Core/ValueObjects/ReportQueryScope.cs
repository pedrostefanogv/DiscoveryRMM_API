namespace Discovery.Core.ValueObjects;

/// <summary>
/// Escopo efetivo aplicado a consulta de um relatorio. Os valores vem do
/// usuario autenticado / da execucao e SOBREPOEM o que o cliente mandou em
/// FiltersJson — sem isso um relatorio de um cliente podia conter dados de
/// todos os clientes.
/// </summary>
public sealed record ReportQueryScope(Guid? ClientId, Guid? SiteId);

/// <summary>Resultado da resolucao de escopo para relatorios.</summary>
public sealed record ReportScopeResolution(
    bool Allowed,
    bool IsGlobal,
    Guid? ClientId,
    Guid? SiteId,
    IReadOnlyList<Guid> AllowedClientIds,
    string? Error);

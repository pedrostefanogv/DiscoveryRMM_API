namespace Discovery.Core.Enums.Identity;

/// <summary>
/// Define como o escopo de autorização deve ser resolvido.
/// </summary>
public enum ScopeSource
{
    /// <summary>Escopo global — não filtra por client/site.</summary>
    Global,

    /// <summary>Resolve clientId e siteId automaticamente dos parâmetros da rota.</summary>
    FromRoute
}

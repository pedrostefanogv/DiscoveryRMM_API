namespace Meduza.Core.Enums.Identity;

/// <summary>
/// Nível de escopo de uma atribuição de role a um grupo de usuários.
/// Global = acesso a todos os clientes/sites.
/// Client = acesso a todos os sites de um cliente específico.
/// Site = acesso a um site específico.
/// </summary>
public enum ScopeLevel
{
    Global,
    Client,
    Site
}

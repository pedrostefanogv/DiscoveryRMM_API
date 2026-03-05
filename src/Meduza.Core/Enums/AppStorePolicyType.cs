namespace Meduza.Core.Enums;

/// <summary>
/// Define a política de acesso à loja de aplicativos.
/// </summary>
public enum AppStorePolicyType
{
    /// <summary>Nenhum acesso à loja de aplicativos</summary>
    Disabled = 0,
    
    /// <summary>Apenas aplicativos pré-aprovados podem ser instalados</summary>
    PreApproved = 1,
    
    /// <summary>Todos os aplicativos disponíveis podem ser instalados</summary>
    All = 2
}

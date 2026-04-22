namespace Discovery.Core.Enums;

/// <summary>
/// Define o escopo de entrega de um alerta PSADT.
/// </summary>
public enum AlertScopeType
{
    /// <summary>Um único agent específico.</summary>
    Agent = 0,

    /// <summary>Todos os agents de um site.</summary>
    Site = 1,

    /// <summary>Todos os agents de um cliente.</summary>
    Client = 2,

    /// <summary>Agents que possuem uma label específica.</summary>
    Label = 3
}

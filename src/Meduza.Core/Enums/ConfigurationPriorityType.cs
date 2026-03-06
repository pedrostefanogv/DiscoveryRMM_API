namespace Meduza.Core.Enums;

/// <summary>
/// Tipo/prioridade de origem de uma configuração efetiva.
/// </summary>
public enum ConfigurationPriorityType
{
    /// <summary>Bloqueado para sobrescrita em níveis inferiores.</summary>
    Block = 0,

    /// <summary>Valor local (reservado para override local futuro).</summary>
    Local = 1,

    /// <summary>Valor definido no servidor/global.</summary>
    Global = 2,

    /// <summary>Valor definido no cliente.</summary>
    Client = 3,

    /// <summary>Valor definido no site.</summary>
    Site = 4,

    /// <summary>Valor definido especificamente para um agent (futuro).</summary>
    Agent = 5
}

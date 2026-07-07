namespace Discovery.Core.Enums;

/// <summary>
/// Tipos de relação entre tickets.
/// </summary>
public enum TicketRelationType
{
    /// <summary>Marca como duplicado (A é duplicado de B)</summary>
    Duplicate = 0,
    /// <summary>Bloqueia (A bloqueia B)</summary>
    Blocks = 1,
    /// <summary>Relacionado a (A está relacionado a B)</summary>
    RelatesTo = 2,
    /// <summary>Ticket pai (A é pai de B)</summary>
    ParentOf = 3,
    /// <summary>Ticket filho (A é filho de B)</summary>
    ChildOf = 4
}

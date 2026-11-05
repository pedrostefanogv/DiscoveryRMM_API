namespace Discovery.Core.ValueObjects;

/// <summary>Desfecho do processamento de uma resolução de sensor.</summary>
public class AutoTicketResolutionResult
{
    /// <summary>Chamado fechado, quando havia um correlacionado.</summary>
    public Guid? ClosedTicketId { get; init; }

    /// <summary>Motivo legível para auditoria.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>True quando um chamado foi efetivamente fechado.</summary>
    public bool Closed { get; init; }
}

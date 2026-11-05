using Discovery.Core.Enums;

namespace Discovery.Core.ValueObjects;

/// <summary>
/// Resultado da aplicação: os valores FINAIS do chamado, já considerando o
/// determinístico, as sugestões aplicadas e o piso de prioridade.
/// </summary>
public class AutoTicketAppliedSuggestion
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public TicketPriority Priority { get; init; } = TicketPriority.Medium;
    public Guid? DepartmentId { get; init; }
    public string? Category { get; init; }

    /// <summary>Sugestões registradas (aplicadas ou não) — viram linhas de auditoria.</summary>
    public IReadOnlyList<AutoTicketSuggestionItem> Suggestions { get; init; } = [];

    /// <summary>True quando a prioridade foi elevada pelo piso de segurança.</summary>
    public bool PriorityFloored { get; init; }

    /// <summary>True quando alguma sugestão foi efetivamente aplicada.</summary>
    public bool AnyApplied => Suggestions.Any(s => s.Applied);
}
